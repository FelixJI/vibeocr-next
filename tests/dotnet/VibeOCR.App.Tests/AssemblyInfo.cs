using Xunit;
using Xunit.Sdk;
using Xunit.v3;

// xunit.v3 4.0 removed the callable CollectionBehavior parallelization
// properties; keep the assembly fully serial as before via Mode=None.
[assembly: Parallelization(Mode = ParallelMode.None)]
