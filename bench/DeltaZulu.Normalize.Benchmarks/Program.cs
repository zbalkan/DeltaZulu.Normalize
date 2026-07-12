using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(DeltaZulu.Normalize.Benchmarks.NormalizationBenchmarks).Assembly).Run(args);