using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NuvioTV.Core.Networking
{
    public static class MapWithConcurrency
    {
        public static async Task<(IReadOnlyList<TOut> Results, int MaxInFlight)> RunAsyncTracked<TIn, TOut>(
            int concurrency,
            IEnumerable<TIn> items,
            Func<TIn, CancellationToken, Task<TOut>> map,
            CancellationToken ct)
        {
            if (items == null)
            {
                throw new ArgumentNullException(nameof(items));
            }
            if (map == null)
            {
                throw new ArgumentNullException(nameof(map));
            }

            var entries = items.ToList();
            if (entries.Count == 0)
            {
                return (Array.Empty<TOut>(), 0);
            }

            var workerCount = Math.Min(entries.Count, Math.Max(1, concurrency));
            var results = new TOut[entries.Count];
            var nextIndex = 0;
            var workers = new Task[workerCount];
            
            // Track in-flight operations
            var currentInFlight = 0;
            var maxInFlight = 0;
            var gate = new object();

            for (var i = 0; i < workerCount; i++)
            {
                workers[i] = Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested)
                    {
                        int index;
                        lock (entries)
                        {
                            index = nextIndex;
                            if (index >= entries.Count)
                            {
                                break;
                            }
                            nextIndex = index + 1;
                        }

                        // Track in-flight
                        var before = Interlocked.Increment(ref currentInFlight);
                        
                        // Update max using CAS loop for thread safety
                        var currentMax = maxInFlight;
                        while (before > currentMax)
                        {
                            var prev = Interlocked.CompareExchange(ref maxInFlight, before, currentMax);
                            if (prev == currentMax)
                            {
                                break; // Successfully updated
                            }
                            currentMax = prev; // Another thread updated, retry
                        }

                        try
                        {
                            results[index] = await map(entries[index], ct);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref currentInFlight);
                        }
                    }
                }, ct);
            }

            await Task.WhenAll(workers);
            return (results, maxInFlight);
        }

        public static async Task<IReadOnlyList<TOut>> RunAsync<TIn, TOut>(
            int concurrency,
            IEnumerable<TIn> items,
            Func<TIn, CancellationToken, Task<TOut>> map,
            CancellationToken ct)
        {
            var (results, _) = await RunAsyncTracked(concurrency, items, map, ct);
            return results;
        }
    }
}