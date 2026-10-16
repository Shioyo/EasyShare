const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const calls = [];
class FakeDecoder {
    state = 'unconfigured';
    decodeQueueSize = 0;
    configure(config) { this.state = 'configured'; calls.push(['configure', config]); }
    decode(chunk) { calls.push(['decode', chunk]); }
    close() { this.state = 'closed'; calls.push(['close']); }
}
const context = { module: { exports: {} }, VideoDecoder: FakeDecoder, EncodedVideoChunk: class { constructor(data) { Object.assign(this, data); } } };
vm.runInNewContext(fs.readFileSync('src/EasyShare/Player/decoder.js', 'utf8'), context);
const { splitAnnexB, annexB, MirrorDecoder } = context.module.exports;
const mixed = new Uint8Array([0, 0, 0, 1, 0x67, 0x42, 0, 0x1f, 0, 0, 1, 0x68, 0xce, 0, 0, 0, 1, 0x65, 0x88]);
assert.equal(splitAnnexB(mixed).length, 3, '3/4-byte start codes');
assert.equal(splitAnnexB(new Uint8Array([1, 2, 3])).length, 0, 'reject unframed bytes');
const d = new MirrorDecoder(() => {}, error => { throw error; });
d.push(new Uint8Array([0, 0, 1, 0x41, 0x80]));
assert.equal(calls.length, 0, 'delta before SPS is ignored');
d.push(annexB([new Uint8Array([0x67, 0x42, 0, 0x1f]), new Uint8Array([0x68, 0xce])]));
assert.equal(calls.length, 0, 'parameter-only callback is cached');
d.push(new Uint8Array([0, 0, 1, 0x41, 0x80]));
assert.equal(calls.length, 0, 'wait for IDR');
d.push(new Uint8Array([0, 0, 1, 0x65, 0x88]));
assert.equal(calls[0][1].codec, 'avc1.42001f');
assert.equal(splitAnnexB(calls[1][1].data).length, 3, 'SPS/PPS prepended to IDR');
d.push(new Uint8Array([0, 0, 1, 0x41, 0x80]));
assert.equal(calls.at(-1)[1].type, 'delta');
d.push(annexB([new Uint8Array([0x67, 0x42, 0, 0x20]), new Uint8Array([0x68, 0xce]), new Uint8Array([0x65, 0x88])]));
assert.equal(calls.filter(c => c[0] === 'configure').length, 2, 'changed SPS rebuilds decoder');
d.decoder.decodeQueueSize = 91;
assert.throws(() => d.push(new Uint8Array([0, 0, 1, 0x41, 0x80])), /积压/, 'do not silently discard reference frames');
d.close();
console.log('PASS: Annex-B parsing, parameter cache, IDR gating, configuration change, bounded decode queue');
