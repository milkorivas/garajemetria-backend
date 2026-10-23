using GarajeMetria.Application.Diagnostics;

namespace GarajeMetria.Infrastructure.Authentication;

internal sealed class AuthenticationAvailability : IAuthenticationAvailability
{
    private readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool available;

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        await initialized.Task.WaitAsync(cancellationToken);
        return available;
    }

    public void SetAvailable(bool value)
    {
        available = value;
        initialized.TrySetResult();
    }
}
