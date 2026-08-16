using System.Runtime.InteropServices;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

namespace WinUIEx.Maps.Benchmarks;

internal static class BenchmarkConfiguration
{
    internal static IConfig Create(string[] arguments)
    {
        string platform = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "ARM64",
            Architecture.X64 => "x64",
            _ => throw new PlatformNotSupportedException(
                "WinUIEx.Maps benchmarks require an ARM64 or x64 process."),
        };
        Job job = GetJob(arguments).WithArguments(
        [
            new MsBuildArgument($"/p:Platform={platform}"),
        ]);
        return ManualConfig
            .Create(DefaultConfig.Instance)
            .AddJob(job);
    }

    internal static string[] RemoveJobArgument(string[] arguments)
    {
        int index = Array.FindIndex(
            arguments,
            argument => string.Equals(
                argument,
                "--job",
                StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return arguments;
        }
        if (index == arguments.Length - 1)
        {
            throw new ArgumentException("--job requires a value.");
        }

        return
        [
            .. arguments.Take(index),
            .. arguments.Skip(index + 2),
        ];
    }

    private static Job GetJob(string[] arguments)
    {
        int index = Array.FindIndex(
            arguments,
            argument => string.Equals(
                argument,
                "--job",
                StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return Job.Default;
        }
        if (index == arguments.Length - 1)
        {
            throw new ArgumentException("--job requires a value.");
        }

        return arguments[index + 1].ToUpperInvariant() switch
        {
            "DRY" => Job.Dry,
            "SHORT" => Job.ShortRun,
            "MEDIUM" => Job.MediumRun,
            "LONG" => Job.LongRun,
            "DEFAULT" => Job.Default,
            _ => throw new ArgumentException(
                "Supported jobs are Dry, Short, Medium, Long, and Default."),
        };
    }
}
