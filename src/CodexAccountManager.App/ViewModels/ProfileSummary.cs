namespace CodexAccountManager.App.ViewModels;

public sealed record ProfileSummary(
    int TotalProfiles,
    int ReadyProfiles,
    int MissingAuthProfiles,
    int ActiveProfiles)
{
    public string TotalText => TotalProfiles.ToString();
    public string ReadyText => ReadyProfiles.ToString();
    public string MissingAuthText => MissingAuthProfiles.ToString();
    public string ActiveText => ActiveProfiles.ToString();
}
