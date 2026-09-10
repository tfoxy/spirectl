namespace Spirectl.Sts2.Core.HotReload;


public interface IHotReloadControl
{
    HotReloadStatusResultSnapshot GetStatus(HotReloadStatusRequestSnapshot request);

    Task<HotReloadOperationResult> RequestReloadAsync(HotReloadRequestSnapshot request);
}
