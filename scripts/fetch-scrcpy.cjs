const fs = require('node:fs');
const crypto = require('node:crypto');

(async () => {
    const name = 'scrcpy-win64-v5.0.1.zip';
    const path = 'tools/downloads/' + name;
    const expected = 'b12a2c4ee8be317422451fc7dcf8ee20a71b5ea7ef9ad73ddd825a25316227a5';
    fs.mkdirSync('tools/downloads', { recursive: true });
    if (!fs.existsSync(path)) {
        const response = await fetch('https://github.com/Genymobile/scrcpy/releases/download/v5.0.1/' + name, { signal: AbortSignal.timeout(180000) });
        if (!response.ok) {
            throw new Error('scrcpy download failed: ' + response.status);
        }
        fs.writeFileSync(path, Buffer.from(await response.arrayBuffer()));
    }
    const actual = crypto.createHash('sha256').update(fs.readFileSync(path)).digest('hex');
    if (actual !== expected) {
        throw new Error('scrcpy archive SHA256 mismatch: ' + actual);
    }
    console.log('Verified official scrcpy 5.0.1 Windows x64 archive: ' + actual);
})().catch(error => {
    console.error(error.message);
    process.exitCode = 1;
});
