'use strict';
const canvas = document.getElementById('screen');
const context = canvas.getContext('2d', { alpha: false, desynchronized: true });
const message = document.getElementById('message');
let frames = 0;
let totalFrames = 0;
let mode = 'h264';
let socket;
let failed = false;
let jpegPending = null;
let jpegDecoding = false;
const selfTest = new URLSearchParams(location.search).has('selftest');
const post = object => window.chrome?.webview?.postMessage(object);

function fail(error) {
    if (failed) {
        return;
    }
    failed = true;
    const text = error.message || String(error);
    message.style.display = 'grid';
    message.querySelector('strong').textContent = '画面暂时不可用';
    message.querySelector('span').textContent = text;
    post({ type: 'error', text });
    if (selfTest) {
        post({ type: 'selftest', passed: false, error: text, totalFrames });
    }
}

function draw(frame) {
    try {
        const width = frame.displayWidth || frame.width;
        const height = frame.displayHeight || frame.height;
        if (canvas.width !== width || canvas.height !== height) {
            canvas.width = width;
            canvas.height = height;
        }
        context.drawImage(frame, 0, 0, width, height);
        canvas.style.display = 'block';
        message.style.display = 'none';
        if (totalFrames === 0) {
            post({ type: 'frame' });
        }
        totalFrames++;
        frames++;
    } finally {
        frame.close();
    }
}

const decoder = new MirrorDecoder(draw, fail);

async function decodeJpeg(bytes) {
    // Independent JPEG images may safely replace older pending images.
    jpegPending = bytes;
    if (jpegDecoding) {
        return;
    }
    jpegDecoding = true;
    try {
        while (jpegPending) {
            const next = jpegPending;
            jpegPending = null;
            draw(await createImageBitmap(new Blob([next], { type: 'image/jpeg' })));
        }
    } catch (error) {
        fail(error);
    } finally {
        jpegDecoding = false;
    }
}

function connect() {
    socket = new WebSocket(new URL('stream', location.href).href.replace('http:', 'ws:'));
    socket.binaryType = 'arraybuffer';
    socket.onmessage = async event => {
        try {
            if (typeof event.data === 'string') {
                const info = JSON.parse(event.data);
                if (info.type === 'mode') {
                    mode = info.text;
                    if (mode === 'h264' && !window.VideoDecoder) {
                        throw new Error('当前 WebView2 不支持视频解码。请更新 Edge 或勾选兼容模式。');
                    }
                } else if (info.type === 'error') {
                    fail(info.text);
                } else if (info.type === 'selftest-ready') {
                    await runSelfTest();
                } else if (totalFrames === 0) {
                    message.querySelector('span').textContent = info.text;
                    post(info);
                }
                return;
            }
            if (failed) {
                return;
            }
            if (mode === 'jpeg') {
                await decodeJpeg(event.data);
            } else {
                decoder.push(new Uint8Array(event.data));
            }
        } catch (error) {
            fail(error);
        }
    };
    socket.onerror = () => fail('无法连接本机投屏服务。请结束投屏后重试。');
    socket.onclose = () => {
        decoder.close();
        fail('USB 或投屏服务已断开，请重新连接。');
    };
}

window.snapshot = () => totalFrames > 0 ? canvas.toDataURL('image/png') : null;
window.addEventListener('keydown', event => {
    if (event.key === 'F11' || event.key === 'Escape') {
        event.preventDefault();
        post({ type: event.key === 'F11' ? 'fullscreen' : 'escape' });
    }
});
window.addEventListener('beforeunload', () => {
    socket?.close();
    decoder.close();
});
setInterval(() => {
    if (totalFrames > 0 && !failed) {
        post({ type: 'stats', width: canvas.width, height: canvas.height, fps: frames });
        frames = 0;
    }
}, 1000);

async function runSelfTest() {
    // Exercise actual H.264 encoding, local WebSocket transport, decoding and rendering.
    const source = document.createElement('canvas');
    source.width = 960;
    source.height = 600;
    const graphics = source.getContext('2d');
    const encoder = new VideoEncoder({
        output(chunk) {
            const bytes = new Uint8Array(chunk.byteLength);
            chunk.copyTo(bytes);
            socket.send(bytes);
        },
        error: fail
    });
    encoder.configure({ codec: 'avc1.42001f', width: 960, height: 600, bitrate: 2500000, framerate: 30, latencyMode: 'realtime', avc: { format: 'annexb' } });
    for (let i = 0; i < 30; i++) {
        graphics.fillStyle = '#163355';
        graphics.fillRect(0, 0, 960, 600);
        graphics.fillStyle = '#55d9b2';
        graphics.fillRect(60 + i * 6, 350, 100, 100);
        graphics.fillStyle = '#ffffff';
        graphics.font = '42px sans-serif';
        graphics.fillText('EasyShare · Video self-test', 60, 140);
        graphics.font = '24px sans-serif';
        graphics.fillText('H.264 → Local WebSocket → WebCodecs', 60, 200);
        graphics.fillText('TEST PATTERN · NOT A CONNECTED TABLET', 60, 250);
        const frame = new VideoFrame(source, { timestamp: i * 33333 });
        encoder.encode(frame, { keyFrame: i % 10 === 0 });
        frame.close();
        await new Promise(resolve => setTimeout(resolve, 33));
    }
    await encoder.flush();
    encoder.close();
    const deadline = performance.now() + 8000;
    while (totalFrames < 30 && performance.now() < deadline) {
        await new Promise(resolve => setTimeout(resolve, 50));
    }
    const pixel = Array.from(context.getImageData(260, 380, 1, 1).data);
    const passed = totalFrames === 30 && canvas.width === 960 && canvas.height === 600 && pixel[1] > 150 && pixel[2] > 100;
    post({ type: 'selftest', passed, totalFrames, width: canvas.width, height: canvas.height, pixel, hardwareTested: false });
}

connect();
