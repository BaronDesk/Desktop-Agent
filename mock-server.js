// BaronDesk Mock WebSocket Server (Node.js - Zero Dependencies)
// Simulates the backend /agent-ws endpoint for Desktop Agent testing.

const http = require('http');
const crypto = require('crypto');
const readline = require('readline');

const PORT = 8443;
const WS_PATH = '/agent-ws';
const WS_GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11';

let activeSocket = null;
let serverSequence = 100;
let currentSessionId = null;

// -------------------------------------------------------------
// HTTP Server & WebSocket Upgrade (RFC 6455)
// -------------------------------------------------------------
const server = http.createServer((req, res) => {
    res.writeHead(200, { 'Content-Type': 'text/plain' });
    res.end('BaronDesk Mock Server running. Connect over WebSocket to ' + WS_PATH);
});

server.on('upgrade', (req, socket, head) => {
    if (req.url !== WS_PATH) {
        socket.destroy();
        return;
    }

    const key = req.headers['sec-websocket-key'];
    if (!key) {
        socket.destroy();
        return;
    }

    const acceptKey = crypto
        .createHash('sha1')
        .update(key + WS_GUID)
        .digest('base64');

    const headers = [
        'HTTP/1.1 101 Switching Protocols',
        'Upgrade: websocket',
        'Connection: Upgrade',
        `Sec-WebSocket-Accept: ${acceptKey}`
    ];

    socket.write(headers.join('\r\n') + '\r\n\r\n');
    activeSocket = socket;

    const authHeader = req.headers['authorization'];
    if (authHeader) {
        console.log(`\n\x1b[32m[AUTH]\x1b[0m Upgrade request contains Authorization: \x1b[36m${authHeader}\x1b[0m`);
    } else {
        console.log('\n\x1b[33m[AUTH]\x1b[0m Upgrade request contains NO Authorization header (unenrolled or fallback).');
    }

    console.log('\x1b[32m[CONNECTED]\x1b[0m Agent connected from ' + socket.remoteAddress);
    printMenu();

    let buffer = Buffer.alloc(0);

    socket.on('data', (chunk) => {
        buffer = Buffer.concat([buffer, chunk]);
        buffer = processFrames(buffer, socket);
    });

    socket.on('close', () => {
        console.log('\n\x1b[31m[DISCONNECTED]\x1b[0m Agent disconnected.');
        activeSocket = null;
        printMenu();
    });

    socket.on('error', (err) => {
        console.error('\x1b[31m[SOCKET ERROR]\x1b[0m', err.message);
    });
});

// -------------------------------------------------------------
// WebSocket Frame Parsing (Client to Server is Masked)
// -------------------------------------------------------------
function processFrames(buf, socket) {
    while (buf.length >= 2) {
        const firstByte = buf[0];
        const opcode = firstByte & 0x0f;
        const secondByte = buf[1];
        const isMasked = (secondByte & 0x80) !== 0;
        let payloadLen = secondByte & 0x7f;
        let offset = 2;

        if (payloadLen === 126) {
            if (buf.length < 4) return buf;
            payloadLen = buf.readUInt16BE(2);
            offset = 4;
        } else if (payloadLen === 127) {
            if (buf.length < 10) return buf;
            payloadLen = Number(buf.readBigUInt64BE(2));
            offset = 10;
        }

        const maskSize = isMasked ? 4 : 0;
        const totalSize = offset + maskSize + payloadLen;

        if (buf.length < totalSize) {
            return buf; // Wait for full frame
        }

        let maskKey = null;
        if (isMasked) {
            maskKey = buf.subarray(offset, offset + 4);
            offset += 4;
        }

        const payload = buf.subarray(offset, offset + payloadLen);
        const unmasked = Buffer.alloc(payloadLen);

        if (isMasked) {
            for (let i = 0; i < payloadLen; i++) {
                unmasked[i] = payload[i] ^ maskKey[i % 4];
            }
        } else {
            payload.copy(unmasked);
        }

        if (opcode === 0x01) {
            handleTextMessage(unmasked.toString('utf8'), socket);
        } else if (opcode === 0x08) {
            socket.end();
            return Buffer.alloc(0);
        } else if (opcode === 0x09) {
            sendFrame(socket, 0x0a, unmasked);
        }

        buf = buf.subarray(totalSize);
    }
    return buf;
}

// -------------------------------------------------------------
// Send Frame (Server to Client is Unmasked)
// -------------------------------------------------------------
function sendFrame(socket, opcode, payload) {
    if (!socket || socket.destroyed) return;

    const payloadBuf = Buffer.isBuffer(payload) ? payload : Buffer.from(payload, 'utf8');
    const len = payloadBuf.length;
    let header;

    if (len <= 125) {
        header = Buffer.alloc(2);
        header[0] = 0x80 | (opcode & 0x0f);
        header[1] = len;
    } else if (len <= 65535) {
        header = Buffer.alloc(4);
        header[0] = 0x80 | (opcode & 0x0f);
        header[1] = 126;
        header.writeUInt16BE(len, 2);
    } else {
        header = Buffer.alloc(10);
        header[0] = 0x80 | (opcode & 0x0f);
        header[1] = 127;
        header.writeBigUInt64BE(BigInt(len), 2);
    }

    socket.write(Buffer.concat([header, payloadBuf]));
}

function sendEnvelope(type, payload, customSeq = null, customTs = null) {
    if (!activeSocket) {
        console.log('\x1b[33m[WARN]\x1b[0m No agent connected.');
        return;
    }

    const envelope = {
        type: type,
        id: crypto.randomUUID(),
        ts: customTs || new Date().toISOString(),
        seq: customSeq !== null ? customSeq : ++serverSequence,
        payload: payload
    };

    const json = JSON.stringify(envelope);
    sendFrame(activeSocket, 0x01, json);

    console.log(`\n\x1b[36m[SERVER -> AGENT]\x1b[0m Sent \x1b[1m${type}\x1b[0m (Seq: ${envelope.seq}, Id: ${envelope.id})`);
    if (payload) {
        console.log('Payload:', JSON.stringify(payload));
    }
}

// -------------------------------------------------------------
// Inbound Message Handler
// -------------------------------------------------------------
function handleTextMessage(text, socket) {
    try {
        const env = JSON.parse(text);
        const type = env.type;
        const seq = env.seq;
        const id = env.id;

        console.log(`\n\x1b[35m[AGENT -> SERVER]\x1b[0m Type: \x1b[1m${type}\x1b[0m | Seq: ${seq} | Id: ${id}`);

        if (type === 'handshake') {
            console.log('\x1b[32m[HANDSHAKE RECEIVED]\x1b[0m Station Details:');
            console.log(JSON.stringify(env.payload, null, 2));

            // Respond with handshake_ack
            sendEnvelope('handshake_ack', { status: 'ACCEPTED', branchId: 'BRANCH-MAIN' });
        } else if (type === 'state_report') {
            console.log('\x1b[36m[STATE_REPORT RECEIVED]\x1b[0m Current Station State:');
            console.log(`   • Locked         : ${env.payload?.locked}`);
            console.log(`   • SessionId      : ${env.payload?.sessionId || 'none'}`);
            console.log(`   • RunningGameId  : ${env.payload?.runningGameId || 'none'}`);
            console.log(`   • LeaseExpiresAt : ${env.payload?.leaseExpiresAt || 'none'}`);
        } else if (type === 'heartbeat') {
            console.log(`\x1b[32m[HEARTBEAT]\x1b[0m Locked: ${env.payload?.locked}, SessionId: ${env.payload?.sessionId || 'null'}`);

            // Respond with heartbeat_ack and 60s lease renewal
            const leaseExpiresAt = new Date(Date.now() + 60 * 1000).toISOString();
            sendEnvelope('heartbeat_ack', {
                leaseExpiresAt: leaseExpiresAt,
                serverTime: new Date().toISOString()
            });
        } else if (type === 'command_ack') {
            console.log(`\x1b[32m[COMMAND_ACK]\x1b[0m Command \x1b[1m${env.payload?.commandId}\x1b[0m executed successfully!`);
        } else if (type === 'command_nack') {
            console.log(`\x1b[31m[COMMAND_NACK]\x1b[0m Command \x1b[1m${env.payload?.commandId}\x1b[0m rejected!`);
            console.log(`Code: ${env.payload?.code}, Reason: ${env.payload?.reason}`);
        } else if (type === 'telemetry') {
            const metrics = env.payload?.metrics || [];
            console.log(`\x1b[34m[TELEMETRY]\x1b[0m Received ${metrics.length} hardware metrics:`);
            if (metrics.length > 0) {
                metrics.forEach(m => {
                    console.log(`   • \x1b[36m${m.metric.padEnd(25)}\x1b[0m : \x1b[32m\x1b[1m${m.value}\x1b[0m`);
                });
            } else {
                console.log('Payload:', JSON.stringify(env.payload, null, 2));
            }
        } else if (type === 'device_event') {
            console.log(`\x1b[33m[DEVICE_EVENT]\x1b[0m ${env.payload?.eventType}: ${env.payload?.deviceName}`);
        } else if (type === 'enroll_request') {
            console.log('\x1b[32m[ENROLL_REQUEST RECEIVED]\x1b[0m Bootstrap Token:', env.payload?.bootstrapToken);
            console.log('Station Metadata:', JSON.stringify(env.payload, null, 2));

            // Generate mock station JWT and respond with enroll_response
            const mockJwt = 'mock.jwt.' + Buffer.from(JSON.stringify({
                stationId: crypto.randomUUID(),
                serial: env.payload?.serialNumber,
                role: 'station',
                iat: Math.floor(Date.now() / 1000),
                exp: Math.floor(Date.now() / 1000) + 86400 * 365
            })).toString('base64url');

            const assignedStationId = crypto.randomUUID();
            sendEnvelope('enroll_response', {
                stationId: assignedStationId,
                stationJwt: mockJwt,
                status: 'ENROLLED',
                message: 'Station successfully enrolled by mock server.'
            });
            console.log(`\x1b[32m[ENROLL_RESPONSE SENT]\x1b[0m Assigned StationId=${assignedStationId}`);
        } else {
            console.log('Payload:', JSON.stringify(env.payload));
        }
    } catch (e) {
        console.log('\x1b[31m[RAW MESSAGE]\x1b[0m', text);
    }
}

// -------------------------------------------------------------
// Interactive Console Menu
// -------------------------------------------------------------
function printMenu() {
    console.log('\n----------------------------------------');
    console.log('BaronDesk Mock Server - Interactive Menu');
    console.log('----------------------------------------');
    console.log(' [1] Send LOCK');
    console.log(' [2] Send UNLOCK (Direct - admin dashboard)');
    console.log(' [3] Send LAUNCH_GAME (gameId: "cs2")');
    console.log(' [4] Send END_SESSION (Ends session & revokes lease)');
    console.log(' [5] Send POLICY_UPDATE');
    console.log(' [6] Send SHUTDOWN');
    console.log(' [7] Test Anti-Replay: Send STALE seq');
    console.log(' [8] Test Unknown Command (Trigger NACK)');
    console.log(' [9] Send UNLOCK with PIN (Booking unlock)');
    console.log(' [0] Simulate Enrollment Rejection');
    console.log(' [q] Quit');
    console.log('----------------------------------------');
}

const rl = readline.createInterface({
    input: process.stdin,
    output: process.stdout
});

rl.on('line', (line) => {
    const choice = line.trim();
    switch (choice) {
        case '1':
            sendEnvelope('LOCK', null);
            break;
        case '2':
            currentSessionId = crypto.randomUUID();
            console.log(`\nUnlocking station (direct) with SessionId=${currentSessionId}`);
            sendEnvelope('UNLOCK', { sessionId: currentSessionId });
            break;
        case '3':
            sendEnvelope('LAUNCH_GAME', { gameId: 'cs2' });
            break;
        case '4':
            sendEnvelope('END_SESSION', { reason: 'user_logout' });
            currentSessionId = null;
            break;
        case '5':
            sendEnvelope('POLICY_UPDATE', { heartbeatIntervalSeconds: 15, telemetryCadenceSeconds: 5 });
            break;
        case '6':
            sendEnvelope('SHUTDOWN', null);
            break;
        case '7':
            console.log('\nSending command with stale sequence (seq: 1)...');
            sendEnvelope('LOCK', null, 1);
            break;
        case '8':
            console.log('\nSending invalid command type "UNKNOWN_TEST"...');
            sendEnvelope('UNKNOWN_TEST', null);
            break;
        case '9': {
            currentSessionId = crypto.randomUUID();
            const pin = '482917';
            console.log(`\nUnlocking station (booking PIN) with SessionId=${currentSessionId}, PIN=${pin}`);
            sendEnvelope('UNLOCK', { sessionId: currentSessionId, pin: pin });
            break;
        }
        case '0':
            console.log('\nSimulating enrollment rejection...');
            sendEnvelope('enroll_response', {
                stationId: crypto.randomUUID(),
                stationJwt: '',
                status: 'REJECTED',
                message: 'Bootstrap token invalid or expired.'
            });
            break;
        case 'q':
            console.log('Exiting mock server...');
            process.exit(0);
            break;
        default:
            printMenu();
            break;
    }
});

server.listen(PORT, '127.0.0.1', () => {
    console.log(`\x1b[32m[READY]\x1b[0m BaronDesk Mock WebSocket Server listening on ws://127.0.0.1:${PORT}${WS_PATH}`);
    console.log('Waiting for BaronDesk Agent to connect...');
    printMenu();
});
