namespace IpLocation.Api.Service;

/// <summary>统一业务发号入口，使用本地 Yitter 算法，不调用外部服务。</summary>
public interface IIdService
{
    long NewId();
}
