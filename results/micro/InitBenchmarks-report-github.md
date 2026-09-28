```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.80GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4
  Job-LRZOYJ : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

MaxIterationCount=60  MinIterationCount=30  

```
| Method                     | Fixture | Parallelism | Mean       | Error     | StdDev    | Median     | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|--------------------------- |-------- |------------ |-----------:|----------:|----------:|-----------:|------:|--------:|-------:|----------:|------------:|
| **Current_Lazy**               | **Async**   | **1**           | **2,872.8 ns** | **179.23 ns** | **400.88 ns** | **2,824.4 ns** |  **1.02** |    **0.21** | **0.0284** |     **526 B** |        **1.00** |
| Final_MaintainerShapeAsync | Async   | 1           | 3,081.6 ns | 197.98 ns | 442.82 ns | 3,066.9 ns |  1.09 |    0.22 | 0.0341 |     604 B |        1.15 |
| Final_WaiterYield          | Async   | 1           | 3,417.0 ns | 210.47 ns | 470.74 ns | 3,436.6 ns |  1.21 |    0.24 | 0.0313 |     623 B |        1.18 |
| Maintainer_Shape           | Async   | 1           | 2,875.0 ns | 204.02 ns | 456.33 ns | 3,002.8 ns |  1.02 |    0.22 | 0.0260 |     487 B |        0.93 |
|                            |         |             |            |           |           |            |       |         |        |           |             |
| **Current_Lazy**               | **Async**   | **16**          | **1,292.3 ns** |  **68.53 ns** | **153.28 ns** | **1,323.0 ns** |  **1.02** |    **0.18** | **0.0293** |     **528 B** |        **1.00** |
| Final_MaintainerShapeAsync | Async   | 16          | 1,355.3 ns |  66.27 ns | 148.22 ns | 1,388.1 ns |  1.06 |    0.18 | 0.0352 |     608 B |        1.15 |
| Final_WaiterYield          | Async   | 16          | 1,316.6 ns |  66.48 ns | 148.69 ns | 1,339.6 ns |  1.03 |    0.18 | 0.0352 |     624 B |        1.18 |
| Maintainer_Shape           | Async   | 16          | 1,280.7 ns |  68.84 ns | 153.96 ns | 1,335.4 ns |  1.01 |    0.18 | 0.0273 |     488 B |        0.92 |
|                            |         |             |            |           |           |            |       |         |        |           |             |
| **Current_Lazy**               | **Sync**    | **1**           |   **260.3 ns** |  **11.71 ns** |  **25.69 ns** |   **253.2 ns** |  **1.01** |    **0.14** | **0.0117** |     **208 B** |        **1.00** |
| Final_MaintainerShapeAsync | Sync    | 1           |   277.0 ns |  10.30 ns |  22.18 ns |   272.4 ns |  1.07 |    0.13 | 0.0093 |     168 B |        0.81 |
| Final_WaiterYield          | Sync    | 1           |   250.7 ns |   9.16 ns |  20.31 ns |   248.3 ns |  0.97 |    0.12 | 0.0093 |     168 B |        0.81 |
| Maintainer_Shape           | Sync    | 1           |   248.0 ns |  12.53 ns |  27.78 ns |   242.9 ns |  0.96 |    0.14 | 0.0093 |     168 B |        0.81 |
|                            |         |             |            |           |           |            |       |         |        |           |             |
| **Current_Lazy**               | **Sync**    | **16**          |   **509.1 ns** |  **29.78 ns** |  **66.61 ns** |   **517.9 ns** |  **1.02** |    **0.20** | **0.0117** |     **208 B** |        **1.00** |
| Final_MaintainerShapeAsync | Sync    | 16          |   514.0 ns |  36.25 ns |  81.08 ns |   515.5 ns |  1.03 |    0.22 | 0.0098 |     168 B |        0.81 |
| Final_WaiterYield          | Sync    | 16          |   543.9 ns |  52.25 ns | 116.86 ns |   558.0 ns |  1.09 |    0.28 | 0.0098 |     168 B |        0.81 |
| Maintainer_Shape           | Sync    | 16          |   531.8 ns |  49.17 ns | 109.98 ns |   538.3 ns |  1.06 |    0.27 | 0.0098 |     168 B |        0.81 |
