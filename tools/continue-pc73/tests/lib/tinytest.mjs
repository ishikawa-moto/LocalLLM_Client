// Minimal test runner: tiny assertion + suite helpers (no framework).
let passed = 0;
let failed = 0;
const failures = [];

export function test(name, fn) {
  try {
    const result = fn();
    if (result && typeof result.then === "function") {
      return result.then(
        () => {
          passed++;
          console.log(`  PASS ${name}`);
        },
        (err) => {
          failed++;
          failures.push({ name, err });
          console.log(`  FAIL ${name}: ${err?.message ?? err}`);
        },
      );
    }
    passed++;
    console.log(`  PASS ${name}`);
  } catch (err) {
    failed++;
    failures.push({ name, err });
    console.log(`  FAIL ${name}: ${err?.message ?? err}`);
  }
  return Promise.resolve();
}

export function assertEqual(actual, expected, message = "") {
  const a = JSON.stringify(actual);
  const b = JSON.stringify(expected);
  if (a !== b) {
    throw new Error(
      `${message ? message + ": " : ""}expected ${b}, got ${a}`,
    );
  }
}

export function assertTrue(value, message = "expected true") {
  if (!value) throw new Error(message);
}

export function assertFalse(value, message = "expected false") {
  if (value) throw new Error(message);
}

export function summary(suiteName) {
  console.log(`\n=== ${suiteName}: ${passed} passed, ${failed} failed ===`);
  if (failed > 0) process.exitCode = 1;
  return { passed, failed };
}
