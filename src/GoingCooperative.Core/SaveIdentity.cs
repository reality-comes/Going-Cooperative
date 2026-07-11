namespace GoingCooperative.Core
{
    public sealed class SaveIdentity
    {
        public SaveIdentity(string name, ulong contentHash, long byteLength)
        {
            Name = name;
            ContentHash = contentHash;
            ByteLength = byteLength;
        }

        public string Name { get; }
        public ulong ContentHash { get; }
        public long ByteLength { get; }

        public override string ToString()
        {
            return $"{Name} bytes={ByteLength} hash={DeterminismHash.Format(ContentHash)}";
        }
    }
}

