namespace GoingCooperative.Core
{
    public sealed class StateHashSample
    {
        public StateHashSample(long tick, ulong hash, string source, int commandCount, string? lastCommand)
        {
            Tick = tick;
            Hash = hash;
            Source = source;
            CommandCount = commandCount;
            LastCommand = lastCommand;
        }

        public long Tick { get; }
        public ulong Hash { get; }
        public string Source { get; }
        public int CommandCount { get; }
        public string? LastCommand { get; }

        public override string ToString()
        {
            return $"tick={Tick} hash={DeterminismHash.Format(Hash)} source={Source} pending={CommandCount} last={LastCommand ?? "<none>"}";
        }
    }
}
