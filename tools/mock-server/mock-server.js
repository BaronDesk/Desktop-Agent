// BaronDesk Mock WebSocket Server (Node.js - Zero Dependencies)
// Simulates the backend /agent-ws endpoint for Desktop Agent testing.
//
// Plain ws:// by default (matches appsettings.Development.json). For wss:// with certificate pinning:
//   MOCK_TLS_CERT=cert.pem MOCK_TLS_KEY=key.pem node mock-server.js
// then copy the printed SHA-256 fingerprint into Agent:PinnedCertificateHash.
//
// Enrollment: POST /enrollment/request answers PENDING until you press [e] (approve) or [x] (reject).
// Hands-free: MOCK_AUTO_APPROVE_ENROLLMENT_SECONDS=5 node mock-server.js approves every new station after 5 s.
//
// Game catalog: GET /stations/me/games serves MOCK_CATALOG; the agent pulls it on connect and after [c] CATALOG_UPDATE.
//
// Login relay: typing PIN 1234 on the lock screen is accepted (login_result + UNLOCK); anything else is rejected.
//
// Hands-free lock screen testing: MOCK_AUTO_RELOCK_SECONDS=20 node mock-server.js
// sends LOCK automatically 20 s after every acknowledged UNLOCK (the fullscreen lock screen covers the terminal).

const fs = require('fs');
const http = require('http');
const https = require('https');
const crypto = require('crypto');
const readline = require('readline');

const PORT = 8443;
const WS_PATH = '/agent-ws';
const ENROLLMENT_PATH = '/enrollment/request';
const CATALOG_PATH = '/stations/me/games';
const SYSTEM32 = `${process.env.SystemRoot || 'C:\\Windows'}\\System32`;

// Sample catalog, as the backend would resolve it for this machine.
const MOCK_CATALOG = [
    // Character Map: a classic single-process exe that holds no user data (Windows 11 Notepad is a stub and restores your documents).
    { gameId: 'charmap', name: 'Character Map (test game)', launchType: 'exe', target: `${SYSTEM32}\\charmap.exe` },
    // Windows 11: calc.exe is a stub that starts CalculatorApp and exits, like a game bootstrapper.
    { gameId: 'calc', name: 'Calculator (bootstrapper test)', launchType: 'exe', target: `${SYSTEM32}\\calc.exe`, processName: 'CalculatorApp.exe' },
    { gameId: 'cs2', name: 'Counter-Strike 2', launchType: 'steam', target: '730', arguments: '-novid', processName: 'cs2.exe' },
    { gameId: 'fortnite', name: 'Fortnite', launchType: 'epic', target: 'Fortnite', processName: 'FortniteClient-Win64-Shipping.exe' },
    { gameId: 'missing', name: 'Not installed here', launchType: 'exe', target: 'D:\\Games\\Missing\\missing.exe' },
    { gameId: 'invalid', name: 'Invalid entry', launchType: 'exe', target: 'relative\\game.exe' }
];
const WS_GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11';
const ACCEPTED_PIN = '1234';
const LEASE_SECONDS = 60;

const AUTO_RELOCK_SECONDS = Number(process.env.MOCK_AUTO_RELOCK_SECONDS) || 0;
const AUTO_APPROVE_ENROLLMENT_SECONDS = Number(process.env.MOCK_AUTO_APPROVE_ENROLLMENT_SECONDS) || 0;

let activeSocket = null;
let serverSequence = 100;
let currentSessionId = null;
const pendingUnlockIds = new Set();
const enrollments = new Map(); // serialNumber -> { publicKey, status, machineId?, stationToken? }
let pendingEnrollmentSerial = null;

function sendUnlock() {
    currentSessionId = crypto.randomUUID();
    console.log(`\nUnlocking station with SessionId=${currentSessionId}`);
    const id = sendEnvelope('UNLOCK', { sessionId: currentSessionId, leaseSeconds: LEASE_SECONDS });
    if (id) {
        pendingUnlockIds.add(id);
    }
}

// -------------------------------------------------------------
// HTTP(S) Server & WebSocket Upgrade (RFC 6455)
// -------------------------------------------------------------
const tlsCert = process.env.MOCK_TLS_CERT;
const tlsKey = process.env.MOCK_TLS_KEY;
const useTls = Boolean(tlsCert && tlsKey);

const requestHandler = (req, res) => {
    if (req.method === 'POST' && req.url === ENROLLMENT_PATH) {
        handleEnrollmentRequest(req, res);
        return;
    }

    if (req.method === 'GET' && req.url === CATALOG_PATH) {
        handleCatalogRequest(req, res);
        return;
    }

    res.writeHead(200, { 'Content-Type': 'text/plain' });
    res.end('BaronDesk Mock Server running. Connect over WebSocket to ' + WS_PATH);
};

// -------------------------------------------------------------
// Game catalog (GET /stations/me/games) - pulled by the agent on every connect and after CATALOG_UPDATE
// -------------------------------------------------------------
// The real backend resolves Game + MachineGame (per-machine overrides) for the station in the JWT.
function handleCatalogRequest(req, res) {
    const hasToken = /^Bearer \S+/.test(req.headers.authorization || '');
    console.log(`\n\x1b[35m[CATALOG]\x1b[0m GET ${CATALOG_PATH} (station credential: ${hasToken ? 'present' : 'MISSING'})`);
    if (!hasToken) {
        res.writeHead(401, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ reason: 'station credential required' }));
        return;
    }

    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ games: MOCK_CATALOG }));
}

// -------------------------------------------------------------
// Enrollment (POST /enrollment/request) - the station's first contact
// -------------------------------------------------------------
// The agent re-posts the same signed request while PENDING. Like the real backend should, the mock:
//   - verifies the ECDSA signature against the public key in the request (proof of possession),
//   - pins the public key recorded on the first request (a later request with another key is refused),
//   - only releases the station token after approval ([e] in the menu; [x] rejects).
function handleEnrollmentRequest(req, res) {
    let body = '';
    req.on('data', (chunk) => {
        body += chunk;
        if (body.length > 64 * 1024) req.destroy();
    });
    req.on('end', () => {
        const reply = (status, json) => {
            res.writeHead(status, { 'Content-Type': 'application/json' });
            res.end(JSON.stringify(json));
        };

        let request;
        try {
            request = JSON.parse(body);
        } catch {
            return reply(400, { status: 'REJECTED', reason: 'invalid JSON' });
        }

        const signingInput = ['BARONDESK-ENROLL-V1', request.oneTimeToken, request.serialNumber, request.mac,
            request.ip, request.agentPublicKey, request.signedAt].join('\n');
        let signatureValid = false;
        try {
            const publicKey = crypto.createPublicKey({ key: Buffer.from(request.agentPublicKey, 'base64'), format: 'der', type: 'spki' });
            signatureValid = crypto.verify('sha256', Buffer.from(signingInput, 'utf8'), publicKey, Buffer.from(request.signature, 'base64'));
        } catch {
            signatureValid = false;
        }

        const serial = request.serialNumber;
        let enrollment = enrollments.get(serial);
        console.log(`\n\x1b[35m[ENROLLMENT]\x1b[0m serial=${serial} mac=${request.mac} ip=${request.ip} ` +
            `token=${request.oneTimeToken ? '(present, hidden)' : '(missing)'} signature=${signatureValid ? 'VALID' : 'INVALID'}`);

        if (!signatureValid || !request.oneTimeToken) {
            return reply(403, { status: 'REJECTED', reason: 'invalid signature or token' });
        }

        if (!enrollment) {
            enrollment = { publicKey: request.agentPublicKey, status: 'PENDING' };
            enrollments.set(serial, enrollment);
            pendingEnrollmentSerial = serial;
            console.log(`\x1b[33m[ENROLLMENT]\x1b[0m New station ${serial} is PENDING. Press [e] to approve or [x] to reject.`);
            if (AUTO_APPROVE_ENROLLMENT_SECONDS > 0) {
                setTimeout(() => decideEnrollment(true), AUTO_APPROVE_ENROLLMENT_SECONDS * 1000);
            }
        } else if (enrollment.publicKey !== request.agentPublicKey) {
            console.log('\x1b[31m[ENROLLMENT]\x1b[0m Public key differs from the first request: refused.');
            return reply(403, { status: 'REJECTED', reason: 'station key mismatch' });
        }

        if (enrollment.status === 'ENROLLED') {
            console.log(`\x1b[32m[ENROLLMENT]\x1b[0m Issuing the station token to ${serial}.`);
            return reply(200, { status: 'ENROLLED', machineId: enrollment.machineId, stationToken: enrollment.stationToken });
        }

        return reply(200, { status: enrollment.status, reason: enrollment.status === 'REJECTED' ? 'declined by admin' : undefined });
    });
}

function decideEnrollment(approve) {
    const enrollment = pendingEnrollmentSerial && enrollments.get(pendingEnrollmentSerial);
    if (!enrollment || enrollment.status !== 'PENDING') {
        console.log('\x1b[33m[WARN]\x1b[0m No pending enrollment.');
        return;
    }

    if (approve) {
        enrollment.status = 'ENROLLED';
        enrollment.machineId = crypto.randomUUID();
        // Fake station JWT (unsigned mock value), handed out on the agent's next poll.
        const encode = (value) => Buffer.from(JSON.stringify(value)).toString('base64url');
        enrollment.stationToken = `${encode({ alg: 'none', typ: 'JWT' })}.${encode({ sub: enrollment.machineId, serial: pendingEnrollmentSerial })}.mock`;
    } else {
        enrollment.status = 'REJECTED';
    }

    console.log(`\n\x1b[36m[ENROLLMENT]\x1b[0m ${pendingEnrollmentSerial} -> ${enrollment.status} (delivered on the agent's next poll).`);
}

const server = useTls
    ? https.createServer({ cert: fs.readFileSync(tlsCert), key: fs.readFileSync(tlsKey) }, requestHandler)
    : http.createServer(requestHandler);

if (useTls) {
    const fingerprint = new crypto.X509Certificate(fs.readFileSync(tlsCert)).fingerprint256;
    console.log(`\x1b[33m[TLS]\x1b[0m Pin this certificate: PinnedCertificateHash = "${fingerprint}"`);
}

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

    console.log('\n\x1b[32m[CONNECTED]\x1b[0m Agent connected from ' + socket.remoteAddress);

    // Show whether a station credential was presented, without ever printing it.
    const authorization = req.headers['authorization'];
    console.log(authorization && authorization.startsWith('Bearer ')
        ? `\x1b[32m[AUTH]\x1b[0m Station credential presented (Bearer, ${authorization.length - 7} chars, value hidden)`
        : '\x1b[33m[AUTH]\x1b[0m No station credential presented');
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

    return envelope.id;
}

// -------------------------------------------------------------
// Inbound Message Handler
// -------------------------------------------------------------
function printPeripherals(list) {
    if (!Array.isArray(list)) return;
    if (list.length === 0) console.log('   • (no watched USB devices)');
    list.forEach(p => {
        const state = p.connected ? '[32mconnected[0m' : '[31mDISCONNECTED[0m';
        console.log(`   • ${String(p.name).padEnd(30)} ${p.vendorProductId}  ${state} since ${p.changedAt}`);
    });
}

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

            // serverTime lets the agent measure its clock offset (venue PCs have no NTP).
            sendEnvelope('handshake_ack', { serverTime: new Date().toISOString() });
        } else if (type === 'login_request') {
            // The agent relays the credential; the server decides. Never log real credentials in a real backend.
            const accepted = env.payload?.credential === ACCEPTED_PIN;
            console.log(`\x1b[35m[LOGIN_REQUEST]\x1b[0m method=${env.payload?.method} -> ${accepted ? 'ACCEPTED' : 'REJECTED'}`);
            sendEnvelope('login_result', {
                requestId: id,
                accepted: accepted,
                reason: accepted ? null : 'Incorrect PIN. Please try again.'
            });
            if (accepted) {
                sendUnlock();
            }
        } else if (type === 'state_report') {
            console.log('\x1b[36m[STATE_REPORT RECEIVED]\x1b[0m Current Station State:');
            console.log(`   • Locked         : ${env.payload?.locked}`);
            console.log(`   • SessionId      : ${env.payload?.sessionId || 'none'}`);
            console.log(`   • RunningGameId  : ${env.payload?.runningGameId || 'none'}`);
            console.log(`   • LeaseExpiresAt : ${env.payload?.leaseExpiresAt || 'none'}`);
            printPeripherals(env.payload?.peripherals);
        } else if (type === 'heartbeat') {
            console.log(`\x1b[32m[HEARTBEAT]\x1b[0m Locked: ${env.payload?.locked}, SessionId: ${env.payload?.sessionId || 'null'}`);

            // Lease renewal. The agent computes the lease from server-clock values only.
            sendEnvelope('heartbeat_ack', {
                leaseSeconds: LEASE_SECONDS,
                serverTime: new Date().toISOString()
            });
        } else if (type === 'command_ack') {
            console.log(`\x1b[32m[COMMAND_ACK]\x1b[0m Command \x1b[1m${env.payload?.commandId}\x1b[0m executed successfully!`);
            if (pendingUnlockIds.delete(env.payload?.commandId) && AUTO_RELOCK_SECONDS > 0) {
                console.log(`\x1b[33m[AUTO-RELOCK]\x1b[0m Sending LOCK in ${AUTO_RELOCK_SECONDS}s...`);
                setTimeout(() => sendEnvelope('LOCK', { reason: 'auto_relock_test' }), AUTO_RELOCK_SECONDS * 1000);
            }
        } else if (type === 'command_nack') {
            console.log(`\x1b[31m[COMMAND_NACK]\x1b[0m Command \x1b[1m${env.payload?.commandId}\x1b[0m rejected!`);
            console.log(`Code: ${env.payload?.code}, Reason: ${env.payload?.reason}`);
        } else if (type === 'telemetry') {
            const samples = env.payload?.samples || [];
            console.log(`\x1b[34m[TELEMETRY]\x1b[0m Received ${samples.length} changed sample(s):`);
            samples.forEach(s => {
                console.log(`   • \x1b[36m${s.metric.padEnd(30)}\x1b[0m : \x1b[32m\x1b[1m${s.value}\x1b[0m`);
            });
        } else if (type === 'catalog_status') {
            const games = env.payload?.games || [];
            console.log(`\x1b[36m[CATALOG_STATUS]\x1b[0m ${games.filter(g => g.installed).length}/${games.length} game(s) launchable on this station:`);
            games.forEach(g => {
                const state = g.installed ? '\x1b[32minstalled\x1b[0m' : `\x1b[31mnot launchable\x1b[0m (${g.reason})`;
                console.log(`   • \x1b[1m${String(g.gameId).padEnd(10)}\x1b[0m ${state}`);
            });
        } else if (type === 'installed_games') {
            const games = env.payload?.games || [];
            console.log(`\x1b[36m[INSTALLED_GAMES]\x1b[0m ${games.length} launcher game(s) installed, ${games.filter(g => !g.inCatalog).length} not in the catalog:`);
            games.forEach(g => {
                const tag = g.inCatalog ? '\x1b[32min catalog\x1b[0m' : '\x1b[33mnot in catalog\x1b[0m';
                console.log(`   • ${String(g.name).padEnd(34)} ${g.launchType}:${g.target}${g.processName ? ` (${g.processName})` : ''}  ${tag}`);
            });
        } else if (type === 'peripheral_status') {
            console.log('\x1b[36m[PERIPHERAL_STATUS]\x1b[0m Watched USB devices:');
            printPeripherals(env.payload?.peripherals);
        } else if (type === 'alert') {
            const a = env.payload || {};
            console.log(`\x1b[31m[ALERT]\x1b[0m ${a.category} / ${a.type} / ${a.severity}: ${a.detail}`);
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
    console.log(` [2] Send UNLOCK (starts a session, ${LEASE_SECONDS}s lease)`);
    console.log(' [3] Send LAUNCH_GAME (gameId: "charmap", Character Map)');
    console.log(' [l] Send LAUNCH_GAME (gameId: "calc", tracked by processName after its stub exits)');
    console.log(' [s] Send LAUNCH_GAME (gameId: "cs2", Steam; NACK unless CS2 is installed)');
    console.log(' [c] Send CATALOG_UPDATE (agent re-downloads the catalog, answers with catalog_status)');
    console.log(' [4] Send END_SESSION (ends the current session)');
    console.log(' [5] Send POLICY_UPDATE');
    console.log(' [6] Send SHUTDOWN');
    console.log(' [7] Test anti-replay: LOCK with a reused seq (expect STALE)');
    console.log(' [8] Test unknown command (expect UNKNOWN_TYPE)');
    console.log(' [9] Test clock drift: UNLOCK stamped 10 min ago (expect STALE)');
    console.log(' [0] Test clock drift: LOCK stamped 10 min ago (expect ACK: locking is always safe)');
    console.log(' [m] Test malformed frame (expect it to be ignored, connection kept)');
    console.log(' [p] Test invalid POLICY_UPDATE (typo in a field name, expect INVALID_PAYLOAD)');
    console.log(' [a] POLICY_UPDATE: enable USB anti-theft alerts (5s debounce)');
    console.log(' [t] POLICY_UPDATE: temperature thresholds 30°C (expect TEMPERATURE_WARNING alerts)');
    console.log(' [r] POLICY_UPDATE: restore default thresholds (85°C)');
    console.log(' [w] session_notice LOW_BALANCE: 3 min left (needs an active session, press 2 first)');
    console.log(' [k] session_notice CLEAR (hide the notice)');
    console.log(' [e] Approve the pending enrollment (station token issued on the next poll)');
    console.log(' [x] Reject the pending enrollment');
    console.log(' [q] Quit');
    console.log(` Lock-screen PIN accepted by this mock: ${ACCEPTED_PIN}`);
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
            sendUnlock();
            break;
        case '3':
            sendEnvelope('LAUNCH_GAME', { gameId: 'charmap' });
            break;
        case 'l':
            sendEnvelope('LAUNCH_GAME', { gameId: 'calc' });
            break;
        case 's':
            sendEnvelope('LAUNCH_GAME', { gameId: 'cs2' });
            break;
        case 'c':
            sendEnvelope('CATALOG_UPDATE', {});
            break;
        case '4':
            sendEnvelope('END_SESSION', { sessionId: currentSessionId, reason: 'user_logout' });
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
        case '9':
            sendEnvelope('UNLOCK', { sessionId: crypto.randomUUID() }, null, new Date(Date.now() - 600000).toISOString());
            break;
        case '0':
            sendEnvelope('LOCK', { reason: 'drift_test' }, null, new Date(Date.now() - 600000).toISOString());
            break;
        case 'p':
            sendEnvelope('POLICY_UPDATE', { heartbeatIntervalSecs: 10 });
            break;
        case 'a':
            sendEnvelope('POLICY_UPDATE', { enableAntiTheftAlerts: true, usbDebounceWindowSeconds: 5 });
            break;
        case 't':
            sendEnvelope('POLICY_UPDATE', { cpuTempAlertThreshold: 30, gpuTempAlertThreshold: 30 });
            break;
        case 'r':
            sendEnvelope('POLICY_UPDATE', { cpuTempAlertThreshold: 85, gpuTempAlertThreshold: 85 });
            break;
        case 'm':
            if (activeSocket) {
                sendFrame(activeSocket, 0x01, '{ "type": "LOCK", "this is not": valid json');
                console.log('\nSent a malformed frame.');
            }
            break;
        case 'w':
            if (!currentSessionId) { console.log('No active session: press 2 (UNLOCK) first.'); break; }
            sendEnvelope('session_notice', {
                sessionId: currentSessionId,
                kind: 'LOW_BALANCE',
                endsAt: new Date(Date.now() + 3 * 60_000).toISOString()
            });
            break;
        case 'k':
            if (!currentSessionId) { console.log('No active session.'); break; }
            sendEnvelope('session_notice', { sessionId: currentSessionId, kind: 'CLEAR' });
            break;
        case 'e':
            decideEnrollment(true);
            break;
        case 'x':
            decideEnrollment(false);
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
    console.log(`\x1b[32m[READY]\x1b[0m BaronDesk Mock WebSocket Server listening on ${useTls ? 'wss' : 'ws'}://127.0.0.1:${PORT}${WS_PATH}`);
    if (AUTO_RELOCK_SECONDS > 0) {
        console.log(`\x1b[33m[AUTO-RELOCK]\x1b[0m On: LOCK is sent ${AUTO_RELOCK_SECONDS}s after every acknowledged UNLOCK.`);
    }
    console.log('Waiting for BaronDesk Agent to connect...');
    printMenu();
});
