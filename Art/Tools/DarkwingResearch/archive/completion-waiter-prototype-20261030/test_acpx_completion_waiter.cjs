// Tests the subscription and delivery contract; the functions.exec host needs a separate live check.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const path = require("node:path");
const scope = {};
vm.runInNewContext(fs.readFileSync(path.join(__dirname, "acpx_completion_waiter.js"), "utf8"), scope);
const wait = scope.waitForExecutorCompletions;
const deferred = () => { let resolve; const promise = new Promise(r => { resolve = r; }); return {promise, resolve}; };
const tick = () => new Promise(r => setImmediate(r));

(async () => {
  const first = deferred(), last = deferred(), events = [], saved = new Map(), subscribed = [];
  const host = {
    writeStdin: a => { subscribed.push(a.session_id); return a.session_id === 1 ? first.promise : last.promise; },
    store: (k, v) => saved.set(k, v), text: e => events.push(e),
    yieldControl: async () => events.push({event: "host_yield"})
  };
  let complete = false;
  const run = wait([{task: "slow", sessionId: 2}, {task: "fast", sessionId: 1}], host, "test")
    .then(() => { complete = true; });
  assert.deepEqual(subscribed, [2, 1]);
  first.resolve({exit_code: 0, output: "first done"});
  await tick();
  assert.equal(complete, false);
  assert.equal(events[0].task, "fast");
  assert.equal(events[1].event, "host_yield");
  assert.equal(events[0].outputTail, "first done");
  assert.deepEqual(Array.from(events[0].remainingTasks), ["slow"]);
  assert.equal(saved.size, 0); // Live results are delivered by events, not a concurrent store read.
  last.resolve({exit_code: 7, output: "second failed"});
  await run;
  assert.equal(saved.get("acpx-completion-batch:test").find(r => r.task === "slow").exitCode, 7);
  assert.equal(events.filter(e => e.event === "executor_completed").length, 2);

  let calls = 0;
  const output = [];
  await wait([{task: "continued", sessionId: 3}, {task: "broken", sessionId: 4}], {
    writeStdin: async a => {
      if (a.session_id === 4) throw new Error("transport failure");
      return ++calls === 1 ? {session_id: 3, output: "x".repeat(25000)} : {exit_code: 0, output: "final"};
    }, store: (k, v) => saved.set(k, v), text: e => output.push(e), yieldControl: async () => {}
  }, "errors");
  const results = saved.get("acpx-completion-batch:errors");
  assert.match(results.find(r => r.task === "broken").error, /transport failure/);
  const kept = results.find(r => r.task === "continued");
  assert.equal(kept.chunks, 2);
  assert.equal(kept.truncated, true);
  assert.equal(kept.outputTail.length, 24000);
  assert.ok(kept.outputTail.endsWith("final"));
  await assert.rejects(wait([{task: "bad", sessionId: -1}], host, "bad"), /Invalid foreground/);
  console.log("PASS: subscriptions, first completion yields before last, failures, continuation, bounded output, invalid handle");
})().catch(error => { console.error(error); process.exitCode = 1; });
