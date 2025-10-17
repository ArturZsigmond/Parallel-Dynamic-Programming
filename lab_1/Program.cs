using System;
using System.Diagnostics;
using System.Threading;

class Account
{
    public int Balance;
    public readonly object Lock = new();
    public Account(int balance) => Balance = balance;
}

static class Program
{
    // ---- Tunables ----
    static int AccountCount = 2_000;      // number of accounts
    static int InitialBalance = 1_000;    // starting balance per account
    static int ThreadCount = 8;           // worker threads
    static int OpsPerThread = 1_000_00;   // operations (transfers) per worker thread
    static int CheckEveryMs = 500;        // checker frequency (milliseconds)
    // -------------------

    static Account[] accounts = Array.Empty<Account>();
    static long initialTotal;                 // sum of all balances at start
    static volatile bool done = false;        // signal for checker to stop

    static void Main()
    {
        Console.WriteLine("Parallel Bank Transfers (per-account locking, ordered lock protocol)");
        Console.WriteLine($"Accounts: {AccountCount}, Threads: {ThreadCount}, Ops/Thread: {OpsPerThread}\n");

        // Initialize accounts
        accounts = new Account[AccountCount];
        for (int i = 0; i < AccountCount; i++)
            accounts[i] = new Account(InitialBalance);

        initialTotal = (long)AccountCount * InitialBalance;

        // Start a checker thread (periodic invariant check)
        var checker = new Thread(CheckerThread) { IsBackground = true };
        checker.Start();

        // Start worker threads
        var workers = new Thread[ThreadCount];
        var sw = Stopwatch.StartNew();
        for (int t = 0; t < ThreadCount; t++)
        {
            workers[t] = new Thread(TransferWorker) { IsBackground = true };
            workers[t].Start();
        }

        // Wait for workers
        foreach (var w in workers) w.Join();
        sw.Stop();

        // Stop checker
        done = true;
        checker.Join();

        // Final consistency check
        var (ok, total) = SafeTotal();
        Console.WriteLine($"\nFINAL CHECK: total = {total}, expected = {initialTotal} => {(ok ? "OK" : "BROKEN")}");
        Console.WriteLine($"Elapsed: {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"Throughput: {((long)ThreadCount * OpsPerThread * 1000L) / Math.Max(1, sw.ElapsedMilliseconds)} ops/s");
    }

    // Each worker performs random transfers, locking accounts in a global order to avoid deadlock.
    static void TransferWorker()
    {
        // Using Random.Shared (thread-safe in .NET 8). Alternatively: ThreadLocal<Random>.
        for (int k = 0; k < OpsPerThread; k++)
        {
            int i = Random.Shared.Next(AccountCount);
            int j = Random.Shared.Next(AccountCount - 1);
            if (j >= i) j++; // ensure j != i

            // Order locks by index to guarantee a consistent global lock order.
            int a = i < j ? i : j;
            int b = i < j ? j : i;

            // Choose amount (avoid negative balances). If from has 0, skip cheaply.
            int from = i;
            int to = j;

            // Fast read (unsynchronized) is okay just to skip zero transfers; correctness is preserved by real locking below.
            if (accounts[from].Balance == 0) continue;

            int maxAmount;
            lock (accounts[a].Lock)
            {
                lock (accounts[b].Lock)
                {
                    // Re-evaluate with locks held.
                    maxAmount = accounts[from].Balance;
                    if (maxAmount <= 0) continue;

                    int amount = 1 + Random.Shared.Next(maxAmount); // [1..maxAmount]
                    accounts[from].Balance -= amount;
                    accounts[to].Balance   += amount;
                }
            }

            // (Optional) tiny pause to vary scheduling:
            // if ((k & 0xFFFF) == 0) Thread.Yield();
        }
    }

    // Periodic checker: acquires all locks in increasing index order, sums, compares to initialTotal.
    static void CheckerThread()
    {
        var next = Stopwatch.StartNew();
        while (!done)
        {
            if (next.ElapsedMilliseconds >= CheckEveryMs)
            {
                next.Restart();
                var (ok, total) = SafeTotal();
                Console.WriteLine($"[check] total = {total}, expected = {initialTotal} => {(ok ? "OK" : "BROKEN")}");
            }
            Thread.Sleep(10); // keep CPU usage modest
        }
    }

    // Lock-all snapshot in strict order (0..N-1), release in reverse order (N-1..0).
    static (bool ok, long total) SafeTotal()
    {
        // Acquire all locks
        for (int i = 0; i < accounts.Length; i++)
            Monitor.Enter(accounts[i].Lock);

        try
        {
            long sum = 0;
            for (int i = 0; i < accounts.Length; i++)
                sum += accounts[i].Balance;

            return (sum == initialTotal, sum);
        }
        finally
        {
            for (int i = accounts.Length - 1; i >= 0; i--)
                Monitor.Exit(accounts[i].Lock);
        }
    }
}
