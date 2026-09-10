# The test split is by cost and dependency, not by disk

**Decision.** `RoseMcp.UnitTests` may touch disk. What puts a test there is that it runs no MSBuild,
starts no child process, needs no fixture solution, finishes in milliseconds and runs on Linux; what
moves a test out is failing one of those, not writing a temp file. The sentence in `CLAUDE.md` saying
so stands, and the disk-touching tests stay where they are.

**Why the properties that matter are those and not disk.** The fast suite exists to be worth running
on every change, and every one of those properties is load-bearing for that: MSBuild and a fixture
solution are what turn seconds into minutes, a child process is what makes a failure hard to read,
and running on Linux is what proves the broker and worker graph reaches no Windows-only project.
Writing a temp file costs a millisecond and none of that. A rule drawn around disk would move tests
that have every property the fast suite is selected for, and keep tests that have none.

**What the disk-touching tests are actually doing.** They stage a directory and ask what the code
reads out of it: which install layout finds the worker beside the broker and which does not, which
configuration a `rosemcp.json` beside a solution pins, how many log sessions survive a prune. Every
one of those is a claim about a path or a file that exists, and a staged directory is the cheapest
honest way to make it -- the alternative is a filesystem abstraction threaded through code whose
entire job is to look at the filesystem, which tests the abstraction and not the answer.

**What moving them would cost.** The tests would land in a suite that loads real solutions, runs
real design-time builds and takes minutes rather than seconds, and they would leave the only job
that runs on Linux. So a resolver defect on Linux, which is exactly the class those tests catch,
would stop being caught by anything -- and forty-odd fast tests would move behind a wait nobody pays
on every change. That is the fast suite getting worse at the one thing it is for.

**What it costs.** A test in the fast suite may leave a temp directory behind if it is killed
mid-run, and a machine with no writable temp directory cannot run all of it. Both are accepted; each
such test stages under its own uniquely named root and removes it on the way out.
