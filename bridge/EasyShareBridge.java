import com.google.gson.Gson;
import com.huawei.hosscrcpy.api.HosRemoteConfig;
import com.huawei.hosscrcpy.api.HosRemoteDevice;
import com.huawei.hosscrcpy.api.ScreenCapCallback;
import io.netty.bootstrap.ServerBootstrap;
import io.netty.buffer.Unpooled;
import io.netty.channel.*;
import io.netty.channel.nio.NioEventLoopGroup;
import io.netty.channel.socket.SocketChannel;
import io.netty.channel.socket.nio.NioServerSocketChannel;
import io.netty.handler.codec.http.*;
import io.netty.handler.codec.http.websocketx.*;
import java.io.*;
import java.net.InetSocketAddress;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.security.SecureRandom;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicLong;

/** One local, authenticated video-only session. No shell/control endpoint is exposed. */
public final class EasyShareBridge {
    private static final Gson JSON = new Gson();
    private final Map<String, String> args;
    private final String token;
    private final String prefix;
    private final ExecutorService captureThread = Executors.newSingleThreadExecutor();
    private final AtomicBoolean started = new AtomicBoolean();
    private final AtomicBoolean stopping = new AtomicBoolean();
    private final AtomicLong queuedBytes = new AtomicLong();
    private volatile HosRemoteDevice device;
    private volatile Channel viewer;
    private Channel server;
    private int port;

    private EasyShareBridge(Map<String, String> args) {
        this.args = args;
        byte[] secret = new byte[24];
        new SecureRandom().nextBytes(secret);
        token = Base64.getUrlEncoder().withoutPadding().encodeToString(secret);
        prefix = "/" + token + "/";
    }

    public static void main(String[] argv) throws Exception {
        Map<String, String> args = new HashMap<>();
        for (int i = 0; i + 1 < argv.length; i += 2) {
            args.put(argv[i], argv[i + 1]);
        }
        new EasyShareBridge(args).run();
    }

    private void run() throws Exception {
        EventLoopGroup accept = new NioEventLoopGroup(1);
        EventLoopGroup network = new NioEventLoopGroup(2);
        Runtime.getRuntime().addShutdownHook(new Thread(this::stopCapture, "capture-cleanup"));
        try {
            server = new ServerBootstrap().group(accept, network).channel(NioServerSocketChannel.class)
                .childOption(ChannelOption.TCP_NODELAY, true)
                .childHandler(new ChannelInitializer<SocketChannel>() {
                    @Override
                    protected void initChannel(SocketChannel ch) {
                        ch.pipeline().addLast(new HttpServerCodec());
                        ch.pipeline().addLast(new HttpObjectAggregator(4096));
                        ch.pipeline().addLast(new HttpHandler());
                        ch.pipeline().addLast(new WebSocketServerProtocolHandler(prefix + "stream", null, false, isSelfTest() ? 2 * 1024 * 1024 : 4096));
                        ch.pipeline().addLast(new StreamHandler());
                    }
                }).bind("127.0.0.1", 0).sync().channel();
            port = ((InetSocketAddress) server.localAddress()).getPort();
            System.out.println("EASYSHARE_READY http://127.0.0.1:" + port + prefix + "index.html"
                + (isSelfTest() ? "?selftest=1" : ""));
            System.out.flush();
            Thread input = new Thread(() -> {
                try {
                    new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8)).readLine();
                } catch (IOException e) {
                    System.err.println("Parent connection ended: " + e.getMessage());
                }
                shutdown();
            }, "parent-lifetime");
            input.setDaemon(true);
            input.start();
            server.closeFuture().sync();
        } finally {
            stopCapture();
            captureThread.shutdownNow();
            accept.shutdownGracefully(0, 1, TimeUnit.SECONDS).sync();
            network.shutdownGracefully(0, 1, TimeUnit.SECONDS).sync();
        }
        System.exit(0);
    }

    private boolean isSelfTest() {
        return "true".equals(args.get("--selftest"));
    }

    private void shutdown() {
        if (stopping.compareAndSet(false, true)) {
            if (viewer != null) {
                viewer.close();
            }
            server.close();
        }
    }

    private void stopCapture() {
        HosRemoteDevice current = device;
        device = null;
        if (current != null) {
            try {
                if ("jpeg".equals(args.get("--mode"))) {
                    current.stopImageScreenCapture();
                } else {
                    current.stopCaptureScreen();
                }
            } catch (Exception e) {
                System.err.println("Capture cleanup: " + e);
            }
        }
    }

    private void sendText(String type, String text) {
        Map<String, String> message = new HashMap<>();
        message.put("type", type);
        message.put("text", text);
        if (viewer != null && viewer.isActive()) {
            viewer.writeAndFlush(new TextWebSocketFrame(JSON.toJson(message)));
        }
    }

    private void sendVideo(byte[] bytes) {
        Channel target = viewer;
        if (target == null || !target.isActive()) {
            return;
        }
        if (queuedBytes.addAndGet(bytes.length) > 16 * 1024 * 1024) {
            queuedBytes.addAndGet(-bytes.length);
            sendText("error", "视频处理积压，请结束后切换轻量模式重新投屏。");
            target.close();
            return;
        }
        target.writeAndFlush(new BinaryWebSocketFrame(Unpooled.wrappedBuffer(bytes)))
            .addListener(future -> queuedBytes.addAndGet(-bytes.length));
    }

    private void startCapture() {
        if (!started.compareAndSet(false, true)) {
            return;
        }
        captureThread.submit(() -> {
            try {
                if (isSelfTest()) {
                    sendText("selftest-ready", "Local bridge ready");
                    return;
                }
                sendText("status", "正在连接平板，首次启动可能需要十几秒…");
                HosRemoteConfig config = new HosRemoteConfig(args.get("--sn"));
                config.setHdcPath(args.get("--hdc"));
                config.setIp("127.0.0.1");
                config.setScale(Integer.parseInt(args.get("--scale")));
                config.setFrameRate(Integer.parseInt(args.get("--fps")));
                int bitRateMbps = Integer.parseInt(args.getOrDefault("--bitrate", "12"));
                if (bitRateMbps < 1 || bitRateMbps > 80) {
                    throw new IllegalArgumentException("视频码率须在 1–80 Mbps 范围内。");
                }
                config.setBitRate(bitRateMbps * 1000000);
                config.setIFrameInterval(1000);
                HosRemoteDevice current = new HosRemoteDevice(config);
                device = current;
                if (stopping.get()) {
                    stopCapture();
                    return;
                }
                if (!current.isOnline()) {
                    throw new IOException("平板已离线，请检查 USB 连接并允许调试授权。");
                }
                sendText("mode", args.get("--mode"));
                ScreenCapCallback callback = new ScreenCapCallback() {
                    @Override
                    public void onData(ByteBuffer data) {
                        byte[] bytes = new byte[data.remaining()];
                        data.get(bytes);
                        sendVideo(bytes);
                    }

                    @Override
                    public void onReady() {
                        sendText("status", "连接已就绪，请在平板上滑动一下以更新画面。");
                    }

                    @Override
                    public void onException(Throwable error) {
                        System.err.println("Capture error: " + error);
                        sendText("error", "投屏接口返回错误，请查看日志，或结束后尝试兼容模式。");
                    }
                };
                if ("jpeg".equals(args.get("--mode"))) {
                    current.startImageScreenCapture(callback);
                } else {
                    current.startCaptureScreen(callback);
                }
            } catch (Throwable error) {
                error.printStackTrace(System.err);
                sendText("error", "无法启动投屏：" + error.getMessage());
            }
        });
    }

    private final class HttpHandler extends SimpleChannelInboundHandler<FullHttpRequest> {
        @Override
        protected void channelRead0(ChannelHandlerContext ctx, FullHttpRequest request) {
            String path = new QueryStringDecoder(request.uri()).path();
            String host = request.headers().get(HttpHeaderNames.HOST, "");
            if (!host.equals("127.0.0.1:" + port) || !request.method().equals(HttpMethod.GET)) {
                respond(ctx, HttpResponseStatus.FORBIDDEN, new byte[0], "text/plain");
                return;
            }
            if (path.equals(prefix + "stream")) {
                String origin = request.headers().get(HttpHeaderNames.ORIGIN, "");
                if (!origin.equals("http://127.0.0.1:" + port) || viewer != null) {
                    respond(ctx, HttpResponseStatus.FORBIDDEN, new byte[0], "text/plain");
                    return;
                }
                ctx.fireChannelRead(request.retain());
                return;
            }
            Map<String, String> assets = new HashMap<>();
            assets.put("index.html", "text/html; charset=utf-8");
            assets.put("player.js", "text/javascript; charset=utf-8");
            assets.put("decoder.js", "text/javascript; charset=utf-8");
            String name = path.startsWith(prefix) ? path.substring(prefix.length()) : "";
            if (!assets.containsKey(name)) {
                respond(ctx, HttpResponseStatus.NOT_FOUND, new byte[0], "text/plain");
                return;
            }
            try {
                respond(ctx, HttpResponseStatus.OK, Files.readAllBytes(Paths.get(args.get("--player"), name)), assets.get(name));
            } catch (IOException error) {
                System.err.println("Player asset: " + error);
                respond(ctx, HttpResponseStatus.INTERNAL_SERVER_ERROR, new byte[0], "text/plain");
            }
        }

        private void respond(ChannelHandlerContext ctx, HttpResponseStatus status, byte[] bytes, String type) {
            FullHttpResponse response = new DefaultFullHttpResponse(HttpVersion.HTTP_1_1, status, Unpooled.wrappedBuffer(bytes));
            response.headers().set(HttpHeaderNames.CONTENT_TYPE, type);
            response.headers().setInt(HttpHeaderNames.CONTENT_LENGTH, bytes.length);
            response.headers().set(HttpHeaderNames.CACHE_CONTROL, "no-store");
            response.headers().set("X-Content-Type-Options", "nosniff");
            response.headers().set("Referrer-Policy", "no-referrer");
            response.headers().set("Content-Security-Policy", "default-src 'self'; script-src 'self'; style-src 'unsafe-inline'; connect-src 'self' ws://127.0.0.1:" + port + "; frame-ancestors 'none'");
            ctx.writeAndFlush(response).addListener(ChannelFutureListener.CLOSE);
        }
    }

    private final class StreamHandler extends SimpleChannelInboundHandler<WebSocketFrame> {
        @Override
        public void userEventTriggered(ChannelHandlerContext ctx, Object event) {
            if (event instanceof WebSocketServerProtocolHandler.HandshakeComplete) {
                viewer = ctx.channel();
                startCapture();
            }
            super.userEventTriggered(ctx, event);
        }

        @Override
        protected void channelRead0(ChannelHandlerContext ctx, WebSocketFrame frame) {
            // Test mode echoes encoded access units through the real WebSocket transport.
            if (isSelfTest() && frame instanceof BinaryWebSocketFrame) {
                byte[] bytes = new byte[frame.content().readableBytes()];
                frame.content().readBytes(bytes);
                sendVideo(bytes);
            }
        }

        @Override
        public void channelInactive(ChannelHandlerContext ctx) {
            if (ctx.channel() == viewer) {
                shutdown();
            }
            super.channelInactive(ctx);
        }

        @Override
        public void exceptionCaught(ChannelHandlerContext ctx, Throwable error) {
            System.err.println("Stream transport: " + error);
            ctx.close();
        }
    }
}
