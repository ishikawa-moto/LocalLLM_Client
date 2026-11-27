import { randomUUID } from 'node:crypto';
import { request as http } from 'node:http';
import { request as https } from 'node:https';

export const protocol = 'localbrain-handoff/1';

// Keep the same instance across requests from a live supervisor. A fresh supervisor
// cannot recover a live grant by registering with a different instance identifier.
export function workerClient({ endpoint, token, instance, ca }) {
    const base = new URL(endpoint);
    const loopback = base.protocol === 'http:' && base.hostname === '127.0.0.1';
    if ((!loopback && base.protocol !== 'https:') || base.username || base.password
        || base.pathname !== '/' || base.search || base.hash) throw new Error('invalid_coordinator_endpoint');
    if (typeof token !== 'string' || token.length < 32) throw new Error('worker_token_required');
    if (!/^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/.test(instance)) throw new Error('invalid_worker_instance');

    const call = (path, command) => new Promise((resolve, reject) => {
        const payload = command === undefined ? undefined : JSON.stringify(command);
        const send = loopback ? http : https;
        const req = send(new URL(`/handoff/v1/${path}`, base), {
            ca,
            method: payload === undefined ? 'GET' : 'POST',
            headers: { Authorization: `Bearer ${token}`, 'X-LocalBrain-Worker': instance,
                ...(payload === undefined ? {} : { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(payload) }) },
            signal: AbortSignal.timeout(5000),
        }, async (res) => {
            try {
                const chunks = [];
                for await (const chunk of res) chunks.push(chunk);
                const result = JSON.parse(Buffer.concat(chunks).toString('utf8'));
                if (res.statusCode !== 200) throw new Error(result.error ?? `http_${res.statusCode}`);
                resolve(result);
            } catch (error) {
                reject(error);
            }
        });
        req.on('error', reject);
        req.end(payload);
    });

    return {
        health: () => call('health'),
        pending: () => call('tasks'),
        task: (taskId) => call(`tasks/${encodeURIComponent(taskId)}`),
        command: (action, fields = {}, requestId = randomUUID()) => call('command', { ...fields, protocol, action, requestId }),
    };
}
