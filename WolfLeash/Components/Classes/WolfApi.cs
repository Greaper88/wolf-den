using System.Text;
using GamesOnWhales;
using GamesOnWhales.SSE;

namespace WolfLeash.Components.Classes;

public class Api : WolfApi, IDisposable
{
    private readonly ILogger<WolfApi> _logger;
    private readonly ActiveUserManager _activeUserManager;
    private int _connectionErrorCount = 0;
    private DateTime _lastConnectionErrorTime = DateTime.MinValue;
    
    public Api(
        ILogger<WolfApi> logger, 
        IConfiguration configuration, 
        IEnumerable<ISseEventHandler> handlers, 
        ActiveUserManager activeUserManager) : 
        base(logger, configuration, handlers)
    {
        _activeUserManager = activeUserManager;
        _logger = logger;
        
        var builder = new StringBuilder();
        foreach (var handler in handlers)
        {
            builder.Append(' ');
            builder.AppendLine(handler.EventName);
        }
        builder.Length -= builder.Length > 0 ? 1 : 0;
        
        logger.LogInformation("Listening for: \n{event}", builder.ToString());
        
        _activeUserManager.OnActivityStateChanged += ActiveUserManagerOnOnActivityStateChanged;
    }

    private async void ActiveUserManagerOnOnActivityStateChanged(bool hasUser)
    {
        _logger.LogDebug("OnActivityStateChanged {users}", hasUser);
        try
        {
            if (hasUser) await StartAsync(CancellationToken.None);
            else await StopAsync(CancellationToken.None);
        }
        catch (Exception e) { _logger.LogWarning(e, "Wolf event connection is unavailable."); }
    }

    protected override Task OnSseConnectionLostEvent(bool isFatal)
    {
        if (DateTime.Now - _lastConnectionErrorTime > TimeSpan.FromSeconds(30))
        {
            _connectionErrorCount = 0;
            _lastConnectionErrorTime = DateTime.Now;
        }
        if (++_connectionErrorCount <= 3) return base.OnSseConnectionLostEvent(isFatal);
        _logger.LogWarning("Wolf API socket is unavailable. Saved app defaults remain available; event connections will retry.");

        return base.OnSseConnectionLostEvent(isFatal);
    }

    public void Dispose()
    {
        _activeUserManager.OnActivityStateChanged -= ActiveUserManagerOnOnActivityStateChanged;
        GC.SuppressFinalize(this);
    }
}
