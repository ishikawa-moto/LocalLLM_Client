// The installed supervisor owns the process tree, checkout and command sandbox.
// It must persist checkpoints locally and make each method idempotent by (id, epoch).
export function handoffWorker({ host, client, supervisor }) {
    if (!['PC70', 'PC73'].includes(host)) throw new Error('invalid_worker');

    let work = Promise.resolve();
    const serialize = (operation) => {
        const result = work.then(operation);
        work = result.catch(() => {});
        return result;
    };

    async function publish(task) {
        const checkpoint = await supervisor.stopAndCheckpoint(task);
        return client.command('release', { taskId: task.id, epoch: task.epoch, checkpoint });
    }

    async function resume(task) {
        await client.command('check', { taskId: task.id, epoch: task.epoch });
        await supervisor.startOrResume(task);
    }

    return {
        register: () => client.command('register'),
        tick: () => serialize(async () => {
            for (const task of await client.pending()) {
                if (task.phase === 'offered') {
                    await supervisor.prepareCheckout(task);
                    const owned = await client.command('receive', { taskId: task.id, epoch: task.epoch });
                    await resume(owned);
                } else if (task.returnPending) {
                    const draining = await client.command('drain', { taskId: task.id, epoch: task.epoch, reason: 'return' });
                    await publish(draining);
                } else if (task.phase === 'draining') {
                    await publish(task);
                } else if (task.phase === 'active') {
                    await resume(task);
                }
            }
        }),
        requestShutdownHandoff: (taskId) => serialize(async () => {
            if (host !== 'PC73') throw new Error('pc73_only');
            let task = await client.task(taskId);
            if (task.phase === 'active' && task.owner?.host === 'PC73') {
                task = await client.command('drain', { taskId, epoch: task.epoch, reason: 'shutdown' });
            }
            if (task.phase === 'draining' && task.owner?.host === 'PC73') task = await publish(task);
            // A caller must wait for affirmative central confirmation before powering off.
            return { taskPreserved: task.owner?.host === 'PC70' || task.phase === 'complete' };
        }),
    };
}
