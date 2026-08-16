using BenchmarkDotNet.Running;
using WinUIEx.Maps.Benchmarks;

BenchmarkSwitcher
    .FromAssembly(typeof(Program).Assembly)
    .Run(
        BenchmarkConfiguration.RemoveJobArgument(args),
        BenchmarkConfiguration.Create(args));
