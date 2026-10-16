/* HOScrcpy onData supplies complete Annex-B access units; WebSocket preserves each unit. */
function splitAnnexB(bytes) {
    const units = [];
    let start = -1;
    for (let i = 0; i + 2 < bytes.length; i++) {
        let prefix = 0;
        if (bytes[i] === 0 && bytes[i + 1] === 0) {
            if (bytes[i + 2] === 1) {
                prefix = 3;
            } else if (i + 3 < bytes.length && bytes[i + 2] === 0 && bytes[i + 3] === 1) {
                prefix = 4;
            }
        }
        if (prefix > 0) {
            if (start >= 0 && i > start) {
                units.push(bytes.subarray(start, i));
            }
            start = i + prefix;
            i = start - 1;
        }
    }
    if (start >= 0 && start < bytes.length) {
        units.push(bytes.subarray(start));
    }
    return units;
}

function annexB(units) {
    const result = new Uint8Array(units.reduce((sum, unit) => sum + 4 + unit.length, 0));
    let offset = 0;
    for (const unit of units) {
        result.set([0, 0, 0, 1], offset);
        result.set(unit, offset + 4);
        offset += 4 + unit.length;
    }
    return result;
}

class MirrorDecoder {
    constructor(output, error) {
        this.output = output;
        this.error = error;
        this.decoder = null;
        this.sps = null;
        this.pps = null;
        this.signature = '';
        this.waitingForKey = true;
        this.timestamp = 0;
    }

    push(bytes) {
        const units = splitAnnexB(bytes);
        let key = false;
        let picture = false;
        for (const unit of units) {
            const type = unit[0] & 31;
            if (type === 7) {
                this.sps = unit.slice();
            }
            if (type === 8) {
                this.pps = unit.slice();
            }
            if (type === 5) {
                key = true;
            }
            if (type === 1 || type === 5) {
                picture = true;
            }
        }
        if (!picture || !this.sps || !this.pps) {
            return;
        }
        const signature = Array.from(this.sps).join(',') + '/' + Array.from(this.pps).join(',');
        if (signature !== this.signature) {
            this.close();
            this.signature = signature;
            this.waitingForKey = true;
        }
        if (this.waitingForKey && !key) {
            return;
        }
        if (!this.decoder) {
            const codec = 'avc1.' + Array.from(this.sps.subarray(1, 4), value => value.toString(16).padStart(2, '0')).join('');
            this.decoder = new VideoDecoder({ output: this.output, error: this.error });
            this.decoder.configure({ codec, optimizeForLatency: true });
        }
        if (this.decoder.decodeQueueSize > 90) {
            throw new Error('解码积压，请结束后切换轻量模式重试。');
        }
        const data = key ? annexB([this.sps, this.pps, ...units.filter(unit => ![7, 8].includes(unit[0] & 31))]) : bytes;
        this.decoder.decode(new EncodedVideoChunk({ type: key ? 'key' : 'delta', timestamp: this.timestamp, data }));
        this.timestamp += 16667;
        this.waitingForKey = false;
    }

    close() {
        if (this.decoder && this.decoder.state !== 'closed') {
            this.decoder.close();
        }
        this.decoder = null;
    }
}

if (typeof module !== 'undefined') {
    module.exports = { splitAnnexB, annexB, MirrorDecoder };
}
