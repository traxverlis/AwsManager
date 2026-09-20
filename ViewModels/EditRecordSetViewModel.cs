using System.Collections.Generic;
using System.Linq;
using AwsManager.Models;

namespace AwsManager.ViewModels
{
    public class EditRecordSetViewModel : ViewModelBase
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public long Ttl { get; set; }
        public bool IsAlias { get; set; }
        public string AliasZoneId { get; set; } = "";
        public bool EvaluateTargetHealth { get; set; }
        public string Value { get; set; } // A single string for simplicity, for multiple values use newline

        public List<string> RecordTypes { get; } = ["A", "AAAA", "CNAME", "MX", "NS", "PTR", "SOA", "SPF", "SRV", "TXT"];
        public ResourceRecordSetModel OriginalRecord { get; }

        public EditRecordSetViewModel(ResourceRecordSetModel? recordToEdit = null)
        {
            if (recordToEdit != null)
            {
                // Editing an existing record
                OriginalRecord = recordToEdit;
                Name = recordToEdit.Name;
                Type = recordToEdit.Type;
                Ttl = recordToEdit.TTL;
                Value = string.Join("\n", recordToEdit.ResourceRecords);
                IsAlias = recordToEdit.IsAlias;
                AliasZoneId = recordToEdit.Original?.AliasTarget?.HostedZoneId ?? "";
                EvaluateTargetHealth = recordToEdit.Original?.AliasTarget?.EvaluateTargetHealth ?? false;
            }
            else
            {
                // Creating a new record
                OriginalRecord = new ResourceRecordSetModel();
                Name = "";
                Type = "A";
                Ttl = 300;
                Value = "";
            }
        }

        public Amazon.Route53.Model.ResourceRecordSet BuildRecord()
        {
            if (OriginalRecord.Original != null && !OriginalRecord.IsEditable)
                throw new ArgumentException("Cette politique de routage est en lecture seule dans AWS Manager.");
            if (string.IsNullOrWhiteSpace(Name) || Name.Length > 255 || Name.Any(char.IsWhiteSpace))
                throw new ArgumentException("Nom DNS requis, sans espaces et de 255 caracteres maximum.");
            if (!RecordTypes.Contains(Type)) throw new ArgumentException("Type DNS non pris en charge.");
            var values = Value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (values.Length == 0) throw new ArgumentException("Au moins une valeur est requise.");
            var record = new Amazon.Route53.Model.ResourceRecordSet
            {
                Name = Name.Trim(),
                Type = Type,
                HealthCheckId = OriginalRecord.Original?.HealthCheckId
            };
            if (IsAlias)
            {
                if (values.Length != 1 || string.IsNullOrWhiteSpace(AliasZoneId) || values[0].Any(char.IsWhiteSpace))
                    throw new ArgumentException("Alias : une cible DNS et son identifiant de zone sont requis.");
                record.AliasTarget = new Amazon.Route53.Model.AliasTarget { DNSName = values[0], HostedZoneId = AliasZoneId.Trim(), EvaluateTargetHealth = EvaluateTargetHealth };
            }
            else
            {
                if (Ttl < 0 || Ttl > int.MaxValue) throw new ArgumentException("TTL : entier positif ou nul, maximum 2147483647.");
                if (Type is "A" or "AAAA" && values.Any(value => !System.Net.IPAddress.TryParse(value, out var address) ||
                    address.AddressFamily != (Type == "A" ? System.Net.Sockets.AddressFamily.InterNetwork : System.Net.Sockets.AddressFamily.InterNetworkV6)))
                    throw new ArgumentException("Adresse IP incompatible avec le type DNS.");
                record.TTL = Ttl;
                record.ResourceRecords = values.Select(value => new Amazon.Route53.Model.ResourceRecord { Value = value }).ToList();
            }
            return record;
        }
    }
}
