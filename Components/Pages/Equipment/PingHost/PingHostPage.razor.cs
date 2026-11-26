using System.Globalization;
using System.Text.Json;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Services.PingDiagnostics;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using MudBlazor;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment.PingHost;

/// <summary>Показывает и останавливает одну сессию проверки доступности.</summary>
public partial class PingHostPage : IAsyncDisposable
{
    private const string TitleText = "Проверка доступности устройства в сети";
    private const string Scope = "journal";
    private const int LogLimit = 31;
    private static readonly ClayToolbarButtonOptions StartButton = new() { Text = "Запустить", Icon = Icons.Material.Filled.PlayArrow };
    private static readonly ClayToolbarButtonOptions StopButton = new() { Text = "Остановить", Icon = Icons.Material.Filled.Stop };
    private static readonly ClayToolbarButtonOptions CloseButton = new() { Text = "Закрыть", Icon = Icons.Material.Filled.Close };

    /// <summary>Имя или IP из запроса.</summary>
    [SupplyParameterFromQuery(Name = "host")] public string? Host { get; set; }
    [Inject] private DbManager Db { get; set; } = null!;
    [Inject] private AuthenticationStateProvider Authentication { get; set; } = null!;
    [Inject] private PingDiagnosticService Diagnostics { get; set; } = null!;
    [Inject] private PingCircuitLifetime Circuit { get; set; } = null!;
    [Inject] private ClayDraftService Drafts { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;

    private readonly CancellationTokenSource _lifetime = new();
    private readonly ClayEntityFormOptions _formOptions = new() { Class = "ping-diagnostics-" + Guid.NewGuid().ToString("N") };
    private readonly List<PingLine> _lines = [];
    private Employee? CurrentUser { get; set; }
    private ClayDraftRegistration? _registration;
    private IJSObjectReference? _scrollModule;
    private ElementReference _journal;
    private PingRun? _run;
    private string? _boundHost;
    private string _heading = TitleText;
    private string? _status;
    private int _hostRevision;
    private bool _interactive;
    private bool _initializing;
    private bool _stopping;
    private bool _closing;
    private bool _disposed;
    private bool _hasError;
    private bool _scrollPending;
    private bool _journalSaveFailed;
    private int? _journalRetryRevision;
    private int? _journalRecoveryAttemptedRevision;

    private bool RetryingCurrentJournal => _journalRetryRevision == _hostRevision;
    private bool CanStart => _interactive && !_initializing && !_stopping && !_closing && !_disposed &&
        !RetryingCurrentJournal && _run is null && CurrentUser is not null;
    private bool CanRetryJournal => _journalSaveFailed && CanStart && _registration is not null;

    /// <inheritdoc />
    protected override Task OnParametersSetAsync() =>
        _interactive && _boundHost != Host ? SelectHostAsync(Host) : Task.CompletedTask;

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            Circuit.Disconnected += OnDisconnected;
            _interactive = true;
            await SelectHostAsync(Host);
            if (!_disposed) StateHasChanged();
            return;
        }

        if (_disposed) return;
        if (CanRetryJournal && _journalRecoveryAttemptedRevision != _hostRevision)
        {
            _journalRecoveryAttemptedRevision = _hostRevision;
            await RetryJournalAsync();
            if (!_disposed) StateHasChanged();
        }
        if (!_scrollPending || _disposed) return;
        _scrollPending = false;
        var ct = _lifetime.Token;
        try
        {
            _scrollModule ??= await JS.InvokeAsync<IJSObjectReference>("import", ct, "./js/pingHost.js");
            await _scrollModule.InvokeVoidAsync("scrollJournal", ct, _journal);
        }
        catch (JSException) { }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task SelectHostAsync(string? host)
    {
        _boundHost = host;
        _initializing = true;
        var revision = ++_hostRevision;
        var ct = _lifetime.Token;
        var autoStart = false;
        try
        {
            await StopAsync();
            if (_disposed || revision != _hostRevision) return;

            var old = _registration;
            _registration = null;
            if (old is not null) await old.DisposeAsync();
            if (_disposed || revision != _hostRevision) return;

            _lines.Clear();
            _heading = TitleText;
            _status = null;
            _hasError = false;
            _journalSaveFailed = false;
            CurrentUser = null;

            var actor = await ReadEmployeeAsync(ct);
            if (_disposed || revision != _hostRevision) return;
            CurrentUser = actor;
            if (actor is null)
            {
                _status = "Не удалось подтвердить текущего сотрудника.";
                _hasError = true;
                return;
            }

            try
            {
                var key = string.IsNullOrWhiteSpace(host) ? "empty" : host.Trim();
                if (key.Length > 253) key = "invalid";
                var registration = await Drafts.RegisterHandleAsync("ping-host", key,
                    actor.Id.ToString(CultureInfo.InvariantCulture), 1, "." + _formOptions.Class, ct);
                if (_disposed || revision != _hostRevision)
                {
                    await registration.DisposeAsync();
                    return;
                }
                _registration = registration;
                if (registration.Restore.TypedScopes?.TryGetValue(Scope, out var json) == true &&
                    JsonSerializer.Deserialize<Journal>(json) is { Lines: not null } saved)
                {
                    _heading = saved.Heading ?? TitleText;
                    _status = saved.Running ? "Предыдущая проверка прервана. Для нового запуска нажмите «Запустить»." : saved.Status;
                    _hasError = !saved.Running && saved.Error;
                    _lines.AddRange(saved.Lines.TakeLast(LogLimit));
                    _scrollPending = true;
                }
                else
                    autoStart = !registration.Restore.HasDraft;
            }
            catch (Exception e) when (e is JSException or JsonException or InvalidOperationException)
            {
                if (!_disposed && revision == _hostRevision)
                    _status = "Сохранённый журнал недоступен. Проверку можно запустить вручную.";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_disposed && revision == _hostRevision)
            {
                _status = "Не удалось подготовить диагностику.";
                _hasError = true;
            }
        }
        finally
        {
            if (revision == _hostRevision) _initializing = false;
        }

        if (autoStart && revision == _hostRevision && CanStart) await StartAsync();
    }

    private Task StartAsync()
    {
        if (!CanStart) return Task.CompletedTask;

        var run = new PingRun(_lifetime.Token);
        _run = run;
        _lines.Clear();
        _heading = TitleText;
        _status = "Подготовка проверки…";
        _hasError = false;
        run.Completion = RunAsync(run, _boundHost, CurrentUser!.Id);
        return Task.CompletedTask;
    }

    private async Task RunAsync(PingRun run, string? host, int employeeId)
    {
        await Task.Yield();
        var ct = run.Token;
        try
        {
            var actor = await ReadEmployeeAsync(ct);
            if (actor?.Id != employeeId)
            {
                _status = "Не удалось подтвердить текущего сотрудника.";
                _hasError = true;
                return;
            }

            await SaveJournalAsync(true, ct);
            var target = await Diagnostics.ResolveAsync(host, ct);
            if (!IsCurrent(run)) return;
            _heading = target.Header;
            _status = "Проверка выполняется.";
            StateHasChanged();

            while (IsCurrent(run))
            {
                var line = await Diagnostics.AttemptAsync(target, ct);
                if (!IsCurrent(run)) return;

                if (_lines.Count == LogLimit) _lines.RemoveAt(0);
                _lines.Add(line);
                _scrollPending = true;
                StateHasChanged();
                await SaveJournalAsync(true, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (PingDiagnosticException e)
        {
            if (IsCurrent(run))
            {
                _status = e.Message;
                _hasError = true;
            }
        }
        catch (Exception)
        {
            if (IsCurrent(run))
            {
                _status = "Диагностика завершилась ошибкой. Повторите запуск.";
                _hasError = true;
            }
        }
        finally
        {
            if (ReferenceEquals(_run, run))
            {
                if (!_disposed)
                {
                    if (ct.IsCancellationRequested && !_hasError)
                        _status = run.UserStopped ? "Проверка остановлена." : "Проверка прервана после потери связи.";
                    if (!_initializing)
                        await SaveJournalAsync(false, _lifetime.Token);
                }
                _run = null;
                _stopping = false;
                if (!_disposed) StateHasChanged();
            }
            run.Dispose();
        }
    }

    private bool IsCurrent(PingRun run) => !_disposed && ReferenceEquals(_run, run) && !run.Token.IsCancellationRequested;

    private async Task StopAsync()
    {
        if (_run is not { } run) return;
        _stopping = true;
        run.Stop(userStopped: true);
        await run.Completion;
    }

    private void OnDisconnected() => Volatile.Read(ref _run)?.Stop(userStopped: false);

    private async Task CloseAsync()
    {
        if (_closing || _disposed) return;
        _closing = true;
        try
        {
            await StopAsync();
            await JS.InvokeVoidAsync("clayModelEquipmentForm.close", _lifetime.Token);
        }
        catch (JSException) { }
        finally { _closing = false; }
    }

    private async Task<Employee?> ReadEmployeeAsync(CancellationToken ct)
    {
        var identity = (await Authentication.GetAuthenticationStateAsync()).User.Identity;
        ct.ThrowIfCancellationRequested();
        return identity?.IsAuthenticated == true ? await Employee.GetCurrentAsync(Db, identity.Name, ct) : null;
    }

    private bool IsCurrentJournal(ClayDraftRegistration registration, int revision) =>
        !_disposed && revision == _hostRevision && ReferenceEquals(registration, _registration);

    private async Task SaveJournalAsync(bool running, CancellationToken ct)
    {
        if (_registration is not { } registration || ct.IsCancellationRequested) return;
        var revision = _hostRevision;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await registration.SaveTypedScopeAsync(Scope,
                JsonSerializer.Serialize(new Journal(_heading, _status, _hasError, running, _lines.ToArray())), budget.Token);
            SetJournalSaveFailed(registration, revision, false);
        }
        catch (Exception e) when (e is JSException or OperationCanceledException or InvalidOperationException)
        {
            if (!ct.IsCancellationRequested) SetJournalSaveFailed(registration, revision, true);
        }
    }

    private async Task RetryJournalAsync()
    {
        if (!CanRetryJournal || _registration is not { } registration) return;
        var revision = _hostRevision;
        _journalRetryRevision = revision;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        budget.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var remaining = await registration.RetryStorageAsync(null, budget.Token);
            if (remaining is null && IsCurrentJournal(registration, revision))
                await SaveJournalAsync(false, budget.Token);
        }
        catch (Exception e) when (e is JSException or OperationCanceledException or InvalidOperationException) { }
        finally
        {
            if (_journalRetryRevision == revision) _journalRetryRevision = null;
        }
    }

    private void SetJournalSaveFailed(ClayDraftRegistration registration, int revision, bool failed)
    {
        if (!IsCurrentJournal(registration, revision) || _journalSaveFailed == failed) return;
        _journalSaveFailed = failed;
        StateHasChanged();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Circuit.Disconnected -= OnDisconnected;
        await _lifetime.CancelAsync();
        if (_run is { } run) await run.Completion;
        if (_registration is not null) await _registration.DisposeAsync();
        if (_scrollModule is not null)
        {
            try { await _scrollModule.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
        _lifetime.Dispose();
    }

    private sealed record Journal(string? Heading, string? Status, bool Error, bool Running, PingLine[] Lines);

    private sealed class PingRun(CancellationToken lifetime) : IDisposable
    {
        private readonly CancellationTokenSource _source = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        private int _userStopped;

        public CancellationToken Token => _source.Token;
        public Task Completion { get; set; } = Task.CompletedTask;
        public bool UserStopped => Volatile.Read(ref _userStopped) != 0;

        public void Stop(bool userStopped)
        {
            if (userStopped) Volatile.Write(ref _userStopped, 1);
            try { _source.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose() => _source.Dispose();
    }
}

