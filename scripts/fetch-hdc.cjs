// Obtain the Windows toolchain directly from the official OpenHarmony SDK archive.
// Stream the tar.gz, retaining only the Windows toolchain ZIP, without unpacking the full SDK.
const fs = require('node:fs');
const zlib = require('node:zlib');
const { Readable } = require('node:stream');
const url = 'https://repo.huaweicloud.com/openharmony/os/6.0-Release/ohos-sdk-windows_linux-public.tar.gz';
(async () => {
    fs.mkdirSync('tools/downloads', { recursive: true });
    const response = await fetch(url);
    if (!response.ok) {
        throw new Error(`SDK download failed: ${response.status}`);
    }
    const stream = Readable.fromWeb(response.body).pipe(zlib.createGunzip());
    let pending = Buffer.alloc(0);
    let remaining = 0;
    let padding = 0;
    let output = null;
    let matched = false;
    for await (const data of stream) {
        pending = Buffer.concat([pending, data]);
        while (pending.length > 0) {
            if (remaining > 0) {
                const length = Math.min(remaining, pending.length);
                if (output !== null) {
                    fs.writeSync(output, pending.subarray(0, length));
                }
                remaining -= length;
                pending = pending.subarray(length);
                if (remaining > 0) {
                    break;
                }
                if (output !== null) {
                    fs.closeSync(output);
                    output = null;
                    console.log('Extracted Windows toolchain to tools/downloads/toolchains.zip');
                    stream.destroy();
                    return;
                }
            }
            if (padding > 0) {
                const length = Math.min(padding, pending.length);
                padding -= length;
                pending = pending.subarray(length);
                if (padding > 0) {
                    break;
                }
            }
            if (pending.length < 512) {
                break;
            }
            const header = pending.subarray(0, 512);
            pending = pending.subarray(512);
            const name = header.subarray(0, 100).toString().replace(/\0.*$/s, '');
            const prefix = header.subarray(345, 500).toString().replace(/\0.*$/s, '');
            const fullName = prefix ? `${prefix}/${name}` : name;
            remaining = parseInt(header.subarray(124, 136).toString().replace(/\0/g, '').trim(), 8) || 0;
            padding = (512 - remaining % 512) % 512;
            if (name) {
                console.log(fullName, remaining);
            }
            if (/toolchains.*windows.*\.zip$/i.test(fullName)) {
                matched = true;
                output = fs.openSync('tools/downloads/toolchains.zip', 'w');
            }
        }
    }
    if (!matched) {
        throw new Error('Windows toolchain not found in SDK');
    }
})().catch(error => {
    console.error(error);
    process.exitCode = 1;
});
