// Benchmark entry points. Each writes its measurements as JSON under the given directory or file; see README.md.
if (args.Length == 2 && args[0] == "--benchmark-v3") { await StorageV3Scenarios.Benchmark(args[1]); return; }
if (args.Length == 2 && args[0] == "--benchmark-storage") { await StorageBenchmark.Benchmark(args[1]); return; }
if (args.Length == 1 && args[0] == "--benchmark-journal-jobs") { await JournalBenchmark.JobsCost(); return; }
if (args.Length == 3 && args[0] == "--benchmark-journal") { await JournalBenchmark.Run(args[1], args[2]); return; }
if (args.Length == 2 && args[0] == "--benchmark-core") { CorePerformanceBenchmark.Run(args[1]); return; }
if (args.Length == 2 && args[0] == "--benchmark-review") { CorePerformanceBenchmark.RunReview(args[1]); return; }
if (args.Length == 2 && args[0] == "--review-equivalence") { ReviewEquivalence.Run(args[1]); return; }
if (args.Length == 2 && args[0] == "--benchmark-review-stages") { ReviewStageBenchmark.Run(args[1]); return; }
if (args.Length == 2 && args[0] == "--benchmark-core-load") { CorePerformanceBenchmark.RunLoad(args[1]); return; }
if (args.Length == 2 && args[0] == "--load-equivalence-seed") { LoadEquivalence.Seed(args[1]); return; }
if (args.Length == 3 && args[0] == "--load-equivalence") { LoadEquivalence.Run(args[1], args[2]); return; }
if (args.Length == 2 && args[0] == "--benchmark-load-stages") { LoadStageBenchmark.Run(args[1]); return; }
if (args.Length == 2 && args[0] == "--benchmark-memory-load") { MemoryLoadBenchmark.Run(args[1]); return; }
if (args.Length == 2 && args[0] == "--benchmark-performance") { await PerformanceScenarios.Benchmark(args[1]); return; }

Console.Error.WriteLine("Unknown or missing command. See tests/VamSys.Benchmarks/README.md for the benchmark entries.");
Environment.ExitCode = 2;
