```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.9457)
Unknown processor
.NET SDK 10.0.100
  [Host]     : .NET 10.0.0 (10.0.25.52411), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  DefaultJob : .NET 10.0.0 (10.0.25.52411), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI


```
| Method                                 | PropertyCount | Mean        | Error    | StdDev   | Ratio | Allocated | Alloc Ratio |
|--------------------------------------- |-------------- |------------:|---------:|---------:|------:|----------:|------------:|
| **JavaPropertiesReader_ReadAllProperties** | **1000**          |    **24.07 μs** | **0.288 μs** | **0.240 μs** |  **1.00** |      **56 B** |        **1.00** |
| Utf8JsonReader_ReadAllProperties       | 1000          |    11.91 μs | 0.071 μs | 0.066 μs |  0.49 |         - |        0.00 |
|                                        |               |             |          |          |       |           |             |
| **JavaPropertiesReader_ReadAllProperties** | **50000**         | **1,295.26 μs** | **3.756 μs** | **3.513 μs** |  **1.00** |      **57 B** |        **1.00** |
| Utf8JsonReader_ReadAllProperties       | 50000         |   608.10 μs | 3.128 μs | 2.773 μs |  0.47 |         - |        0.00 |
