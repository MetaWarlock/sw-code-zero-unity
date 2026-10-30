// Run inside functions.exec with the supplied host, not as a standalone Node process.
// Every foreground session is subscribed before waiting. A terminal outcome yields immediately;
// other subscriptions stay alive in this same exec cell. No timeout, agent-status or log polling.
async function waitForExecutorCompletions(jobs, host, batch) {
  if (!jobs.length || new Set(jobs.map(j => j.task)).size !== jobs.length)
    throw new Error("Expected nonempty jobs with unique task names");
  for (const job of jobs)
    if (!Number.isInteger(job.sessionId) || job.sessionId <= 0)
      throw new Error("Invalid foreground session for " + job.task);

  const remaining = new Set(jobs.map(j => j.task));
  const completed = [];
  await Promise.all(jobs.map(async job => {
    let result, tail = "", chunks = 0, truncated = false;
    try {
      let sessionId = job.sessionId;
      while (sessionId) {
        const part = await host.writeStdin({session_id: sessionId, chars: "",
          max_output_tokens: 4000, yield_time_ms: 300000});
        chunks++;
        tail += part.output || "";
        if (tail.length > 24000) { tail = tail.slice(-24000); truncated = true; }
        sessionId = part.session_id;
        if (!sessionId) {
          if (!Number.isInteger(part.exit_code)) throw new Error("Missing terminal exit code");
          result = {exitCode: part.exit_code, outputTail: tail, truncated, chunks};
        }
      }
    } catch (error) {
      result = {error: String(error), outputTail: tail, truncated, chunks};
    }
    completed.push({task: job.task, ...result});
    remaining.delete(job.task);
    // The event carries the result. Cross-exec store writes may not become visible until this
    // whole cell finishes, so acceptance of an early result must not depend on host.store/load.
    host.text({event: "executor_completed", task: job.task, batch,
      remainingTasks: [...remaining], ...result});
    await host.yieldControl();
  }));
  const resultKey = "acpx-completion-batch:" + batch;
  host.store(resultKey, completed);
  host.text({event: "all_executors_completed", batch, resultKey});
}
