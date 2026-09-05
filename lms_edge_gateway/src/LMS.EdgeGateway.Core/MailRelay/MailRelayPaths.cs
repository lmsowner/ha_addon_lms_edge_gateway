using Microsoft.Extensions.Options;

namespace LMS.EdgeGateway.Core;

public sealed class MailRelayPaths(IOptions<EdgeGatewayCoreOptions> options)
{
    public string Root
    {
        get
        {
            var dataRoot = options.Value.DataRoot;
            var root = Path.IsPathRooted(dataRoot)
                ? dataRoot
                : Path.GetFullPath(dataRoot);
            return Path.Combine(root, "mail-relay");
        }
    }

    public string StatePath => Path.Combine(Root, "mail-relay.json");
    public string SecretsDirectory => Path.Combine(Root, "secrets");
    public string ConfigDirectory => Path.Combine(Root, "config");
    public string ApplyScriptPath => "/usr/local/bin/lms-mail-relay-apply";
    public string SaslDatabasePath => "/var/lib/lms/sasldb2";
    public string MailLogPath => Path.Combine(Root, "mail.log");
    public string SystemMailLogPath => "/var/log/mail.log";
    public string MessagesLogPath => "/var/log/messages";
}
