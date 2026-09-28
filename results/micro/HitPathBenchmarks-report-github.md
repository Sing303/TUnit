```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.80GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4


```
| Method                     | Mean     | Error    | StdDev   | Ratio | RatioSD | Allocated | Alloc Ratio |
|--------------------------- |---------:|---------:|---------:|------:|--------:|----------:|------------:|
| Current_Lazy               | 22.02 ns | 0.648 ns | 1.891 ns |  1.01 |    0.12 |         - |          NA |
| Maintainer_Shape           | 12.74 ns | 0.387 ns | 1.111 ns |  0.58 |    0.07 |         - |          NA |
| Final_MaintainerShapeAsync | 22.65 ns | 1.007 ns | 2.955 ns |  1.04 |    0.16 |         - |          NA |
| Final_WaiterYield          | 20.97 ns | 0.636 ns | 1.856 ns |  0.96 |    0.12 |         - |          NA |
