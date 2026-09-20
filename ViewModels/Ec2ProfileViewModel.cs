using AwsManager.Services;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace AwsManager.ViewModels;

public sealed class Ec2ProfileViewModel : AwsResourceViewModel, IDisposable
{
    private readonly Ec2ProfileService _ec2;
    private readonly IamService _iam;
    private readonly Func<string, bool> _confirm;
    private readonly CancellationTokenSource _lifetime = new();
    private Ec2ProfileState? _state;
    private InstanceProfileItem? _selectedProfile;
    private bool _isLoading;
    private bool _mustRefresh = true;
    private bool _disposed;
    public string InstanceId { get; }
    public ObservableCollection<InstanceProfileItem> Profiles { get; } = [];
    public InstanceProfileItem? SelectedProfile { get => _selectedProfile; set { SetField(ref _selectedProfile, value); Notify(); } }
    public string CurrentProfile => _state?.ProfileArn is { Length: > 0 } arn ? arn : "Aucun profil associe";
    public string InstanceState => _state?.InstanceState ?? "Inconnu";
    public bool IsLoading { get => _isLoading; private set { SetField(ref _isLoading, value); Notify(); } }
    public bool CanEdit => !_disposed && !IsLoading;
    public bool CanApply => CanEdit && !_mustRefresh && _state != null && SelectedProfile is { RoleArn.Length: > 0 } &&
        SelectedProfile.Arn != _state.ProfileArn && (_state.AssociationId.Length == 0 ? _state.InstanceState is "running" or "stopped" : _state.InstanceState == "running" && _state.AssociationState == "associated") &&
        Allowed([new(_state.AssociationId.Length == 0 ? "ec2:AssociateIamInstanceProfile" : "ec2:ReplaceIamInstanceProfileAssociation", Arn("ec2", $"instance/{InstanceId}")),
            new("iam:GetInstanceProfile", SelectedProfile.Arn), new("iam:PassRole", SelectedProfile.RoleArn, "iam:PassedToService", "ec2.amazonaws.com")], true);
    public ICommand RefreshCommand { get; }
    public ICommand ApplyCommand { get; }

    public Ec2ProfileViewModel(string instanceId, IAwsClientFactory factory, AwsContext? context = null, Func<string, bool>? confirm = null) : base(factory, context)
    {
        InstanceId = instanceId;
        _ec2 = new(factory);
        _iam = new(factory);
        _confirm = confirm ?? Confirm;
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => CanEdit && Allowed("ec2:DescribeInstances,ec2:DescribeIamInstanceProfileAssociations,iam:ListInstanceProfiles"));
        ApplyCommand = new AsyncRelayCommand(_ => ApplyAsync(), _ => CanApply);
    }

    public async Task RefreshAsync()
    {
        if (!CanEdit) return;
        IsLoading = true;
        _mustRefresh = true;
        _state = null;
        SelectedProfile = null;
        Profiles.Clear();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        request.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            if (!await CheckAccessAsync([new("ec2:DescribeInstances"), new("ec2:DescribeIamInstanceProfileAssociations"), new("iam:ListInstanceProfiles")]))
            { Status = "Lecture du profil non autorisee ou non verifiee."; return; }
            var state = await _ec2.ReadAsync(InstanceId, request.Token);
            var profiles = await _iam.ProfilesAsync(request.Token);
            request.Token.ThrowIfCancellationRequested();
            _state = state;
            foreach (var item in profiles) Profiles.Add(item);
            _mustRefresh = false;
            Status = state.AssociationId.Length > 0 && state.InstanceState != "running" ? "Le remplacement exige une instance demarree." : $"{Profiles.Count} profil(s) disponible(s).";
        }
        catch (Exception exception) { if (!_disposed) ReportError(exception); }
        finally { IsLoading = false; Notify(); }
    }

    private async Task ApplyAsync()
    {
        if (!CanApply) return;
        var state = _state!;
        var profile = SelectedProfile!;
        if (!_confirm($"Modifier le profil IAM de {InstanceId} ?\nAvant : {CurrentProfile}\nApres : {profile.Arn}\nRole : {profile.RoleArn}\n\nLes permissions applicatives et l'acces SSM peuvent etre perdus. Aucun changement de politique IAM. Aucun redemarrage demande.")) return;
        IsLoading = true;
        _mustRefresh = true;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        request.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            await _iam.VerifyProfileAsync(profile, request.Token);
            var result = await _ec2.ChangeAsync(state, profile.Arn, request.Token);
            if (!_disposed) Status = result;
        }
        catch (Exception exception)
        {
            if (!_disposed) { ReportError(exception); Status += " Actualisez pour verifier l'association avant toute nouvelle tentative."; }
        }
        finally { IsLoading = false; }
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(CurrentProfile));
        OnPropertyChanged(nameof(InstanceState));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanApply));
        CommandManager.InvalidateRequerySuggested();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        Notify();
    }
}