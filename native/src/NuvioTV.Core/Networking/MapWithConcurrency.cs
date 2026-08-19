using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NuvioTV.Core.Networking
{
    public static class MapWithConcurrency
    {
        public static async Task<IReadOnlyList<TOut>> RunAsync<TIn, TOut>(
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
                return Array.Empty<TOut>();
            }

            var workerCount = Math.Min(entries.Count, Math.Max(1, concurrency));
            var results = new TOut[entries.Count];
            var nextIndex = 0;
            var workers = new Task[workerCount];

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

                        results[index] = await map(entries[index], ct);
                    }
                }, ct);
            }

            await Task.WhenAll(workers);
            return results;
        }
    }
}