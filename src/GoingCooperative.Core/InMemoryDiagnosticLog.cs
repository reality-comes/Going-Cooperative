using System.Collections.Generic;

namespace GoingCooperative.Core
{
    public sealed class InMemoryDiagnosticLog
    {
        private readonly int capacity;
        private readonly Queue<DiagnosticSnapshot> snapshots = new Queue<DiagnosticSnapshot>();

        public InMemoryDiagnosticLog(int capacity)
        {
            this.capacity = capacity <= 0 ? 128 : capacity;
        }

        public void Add(DiagnosticSnapshot snapshot)
        {
            snapshots.Enqueue(snapshot);
            while (snapshots.Count > capacity)
            {
                snapshots.Dequeue();
            }
        }

        public DiagnosticSnapshot? Latest
        {
            get
            {
                DiagnosticSnapshot? latest = null;
                foreach (var snapshot in snapshots)
                {
                    latest = snapshot;
                }

                return latest;
            }
        }
    }
}

