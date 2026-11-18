using Xunit;

namespace CdBurner.Lithography.Tests.Rosetta;

/// <summary>
/// Tests that compare process-wide heap sizes (GC.GetTotalMemory) must not overlap with other tests, whose allocations
/// would skew the peak. xUnit runs collections that disable parallelization one at a time after all parallel
/// collections have finished, so every other test class may run concurrently.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HeapMeasurementCollection
{
    public const string Name = "Heap measurement";
}
