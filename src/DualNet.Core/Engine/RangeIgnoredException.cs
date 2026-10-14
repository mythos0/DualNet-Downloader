using System.IO;

namespace DualNet.Core.Engine;

/// <summary>
/// Thrown when a server that previously acknowledged byte-ranges (probe → 206) answers an
/// actual segment request with a full 200 body — i.e. it ignores the Range header.
/// The job downgrades to a single-stream download instead of failing.
/// </summary>
public sealed class RangeIgnoredException : IOException
{
    public RangeIgnoredException(string message) : base(message) { }
}
