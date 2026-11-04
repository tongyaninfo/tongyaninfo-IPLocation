namespace IpLocation.Api.Options;

/// <summary>位宽与时间基点沿用组件默认值；运行后不得随意改变算法参数。</summary>
public sealed class IdGenerationOptions
{
    public const string SectionName = "IdGeneration";
    public ushort WorkerId { get; set; } = 1;
}
