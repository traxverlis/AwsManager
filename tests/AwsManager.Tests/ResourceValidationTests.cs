using AwsManager.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AwsManager.Tests;

[TestClass]
public class ResourceValidationTests
{
    [TestMethod]
    public void CapacityRejectsInconsistentValues()
    {
        ResourceValidation.Capacity(0, 1, 2);
        Assert.ThrowsException<ArgumentException>(() => ResourceValidation.Capacity(2, 1, 3));
        Assert.ThrowsException<ArgumentException>(() => ResourceValidation.Capacity(-1, 1, 3));
        Assert.ThrowsException<ArgumentException>(() => ResourceValidation.Capacity(0, 4, 3));
    }
    [TestMethod]
    public void TagsRejectDuplicateKeys()
    {
        Assert.ThrowsException<ArgumentException>(() => ResourceValidation.Tags([("Name", "one"), ("Name", "two")]));
        Assert.ThrowsException<ArgumentException>(() => ResourceValidation.Tags([("", "value")]));
    }
    [TestMethod]
    public void RelativeKeyOnlyRemovesLeadingPrefix()
        => Assert.AreEqual("folder/report.txt", ResourceValidation.RelativeKey("folder/folder/report.txt", "folder/"));
    [TestMethod]
    public void SmallFilesAreNotRoundedToZeroMegabytes()
        => Assert.AreEqual("500 o", ResourceValidation.FileSize(500));
    [TestMethod]
    public void SecurityRulePreservesIpv6()
    {
        var rule = SecurityRuleEditor.Parse("tcp", "443", "2001:db8::/64", "https");
        Assert.AreEqual("2001:db8::/64", SecurityRuleEditor.ToPermission(rule).Ipv6Ranges[0].CidrIpv6);
        Assert.IsNull(rule.CidrIpv4);
    }
    [TestMethod]
    public void SecurityRulePreservesGroupReference()
    {
        var permission = SecurityRuleEditor.ToPermission(SecurityRuleEditor.Parse("tcp", "5432", "sg-0123456789abcdef0", "database"));
        Assert.AreEqual("sg-0123456789abcdef0", permission.UserIdGroupPairs[0].GroupId);
        Assert.IsNull(permission.Ipv4Ranges);
    }
    [TestMethod]
    public void InvalidPortsAndCidrRejectedBeforeMutation()
    {
        Assert.ThrowsException<ArgumentException>(() => SecurityRuleEditor.Parse("tcp", "443-80", "0.0.0.0/0", ""));
        Assert.ThrowsException<ArgumentException>(() => SecurityRuleEditor.Parse("tcp", "65536", "0.0.0.0/0", ""));
        Assert.ThrowsException<ArgumentException>(() => SecurityRuleEditor.Parse("tcp", "443", "10.0.0.0/64", ""));
    }
    [TestMethod]
    public void IcmpUsesTypeAndCodeNotPortRange()
    {
        var rule = SecurityRuleEditor.Parse("icmp", "8/0", "10.0.0.0/8", "");
        Assert.AreEqual(8, rule.FromPort);
        Assert.AreEqual(0, rule.ToPort);
        Assert.ThrowsException<ArgumentException>(() => SecurityRuleEditor.Parse("icmp", "-1/0", "10.0.0.0/8", ""));
    }
    [TestMethod]
    public void AllProtocolsDoNotSendPortNumbers()
    {
        var rule = SecurityRuleEditor.Parse("-1", "All", "pl-0123456789abcdef0", "");
        Assert.IsNull(rule.FromPort);
        Assert.IsNull(rule.ToPort);
        Assert.AreEqual("pl-0123456789abcdef0", rule.PrefixListId);
    }

    [TestMethod]
    public void GuidedSecurityRulePresetsValidatePortsAndTargetKind()
    {
        var editor = new AwsManager.ViewModels.SecurityRuleEditorViewModel();
        Assert.IsFalse(editor.CanSubmit);
        editor.SelectedPreset = editor.Presets.Single(preset => preset.Name == "HTTPS");
        editor.Source = "10.0.0.0/8";
        Assert.IsTrue(editor.CanSubmit);
        Assert.AreEqual(443, editor.BuildRule().ToPort);
        editor.To = "80";
        Assert.IsFalse(editor.CanSubmit);
        editor.To = "";
        Assert.IsTrue(editor.CanSubmit);
        editor.SourceKind = "ipv6";
        Assert.IsFalse(editor.CanSubmit);
        editor.Source = "2001:db8::/64";
        Assert.IsTrue(editor.CanSubmit);
        editor.SourceKind = "any6";
        Assert.AreEqual("::/0", editor.BuildRule().CidrIpv6);
        Assert.IsTrue(editor.ExposureWarning.Length > 0);
        editor.SourceKind = "group";
        editor.Source = "sg-0123456789abcdef0";
        Assert.IsTrue(editor.CanSubmit);
        Assert.AreEqual("", editor.ExposureWarning);
    }

    [TestMethod]
    public void GuidedSecurityRulePreservesExistingIcmpAndDirection()
    {
        var original = new AwsManager.Models.SecurityGroupRuleModel { RuleId = "sgr-demo", GroupId = "sg-0123456789abcdef0", Type = "Egress", Protocol = "58", PortRange = "128/0", SourceOrDestination = "2001:db8::/64", Description = "ping" };
        var editor = new AwsManager.ViewModels.SecurityRuleEditorViewModel(original: original);
        Assert.IsTrue(editor.IsIcmp);
        Assert.IsFalse(editor.CanChangeDirection);
        editor.IsEgress = false;
        Assert.AreEqual("Egress", editor.Direction);
        var rule = editor.BuildRule();
        Assert.AreEqual("icmpv6", rule.IpProtocol);
        Assert.AreEqual(128, rule.FromPort);
        Assert.AreEqual(0, rule.ToPort);
        Assert.AreEqual("ping", rule.Description);
        editor.From = "-1";
        Assert.IsFalse(editor.CanSubmit);
        editor.AllPorts = true;
        Assert.AreEqual(-1, editor.BuildRule().ToPort);
    }

    [TestMethod]
    public void GuidedSecurityRulePreservesNumericProtocolAndPrefixList()
    {
        var editor = new AwsManager.ViewModels.SecurityRuleEditorViewModel(original: new()
        { RuleId = "sgr-demo", GroupId = "sg-0123456789abcdef0", Type = "Ingress", Protocol = "47", PortRange = "All", SourceOrDestination = "pl-0123456789abcdef0", Description = "GRE" });
        Assert.IsTrue(editor.IsCustomProtocol);
        Assert.IsFalse(editor.HasPorts);
        Assert.AreEqual("47", editor.BuildRule().IpProtocol);
        Assert.IsNull(editor.BuildRule().FromPort);
        Assert.AreEqual("pl-0123456789abcdef0", editor.BuildRule().PrefixListId);
        editor.ProtocolNumber = "999";
        Assert.IsFalse(editor.CanSubmit);
        editor.Protocol = "-1";
        Assert.IsTrue(editor.CanSubmit);
        Assert.IsNull(editor.BuildRule().ToPort);
    }

    [TestMethod]
    public void SsmUsesSeparatedProfileArgumentsAndExplicitRegion()
    {
        var context = new AwsContext("profile with spaces & chars", "eu-west-1", "000000000000", "demo", new Amazon.Runtime.AnonymousAWSCredentials());
        var start = SsmService.CreateStartInfo(context, "i-0123456789abcdef0", 3389, 13389);
        Assert.AreEqual("aws", start.FileName);
        Assert.IsFalse(start.UseShellExecute);
        CollectionAssert.Contains(start.ArgumentList.ToArray(), context.Profile);
        CollectionAssert.Contains(start.ArgumentList.ToArray(), "eu-west-1");
        Assert.IsFalse(start.ArgumentList.Any(argument => argument.Contains("cmd.exe")));
        var parameters = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string[]>>(start.ArgumentList.Last())!;
        Assert.AreEqual("13389", parameters["localPortNumber"][0]);
    }

    [TestMethod]
    public void RemoteHostTunnelUsesDedicatedDocumentAndStructuredParameters()
    {
        var context = new AwsContext("demo profile", "eu-west-1", "000000000000", "fake", new Amazon.Runtime.AnonymousAWSCredentials());
        var start = SsmService.CreateStartInfo(context, "i-0123456789abcdef0", 5432, 15432, "database.example.test");
        CollectionAssert.Contains(start.ArgumentList.ToArray(), "AWS-StartPortForwardingSessionToRemoteHost");
        Assert.IsFalse(start.UseShellExecute);
        var parameters = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string[]>>(start.ArgumentList.Last())!;
        Assert.AreEqual("database.example.test", parameters["host"][0]);
        Assert.AreEqual("5432", parameters["portNumber"][0]);
        Assert.AreEqual("15432", parameters["localPortNumber"][0]);
        var legacy = SsmService.CreateStartInfo(context, "i-0123456789abcdef0", 5432, 15432);
        CollectionAssert.Contains(legacy.ArgumentList.ToArray(), "AWS-StartPortForwardingSession");
        Assert.IsFalse(System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string[]>>(legacy.ArgumentList.Last())!.ContainsKey("host"));
        foreach (var invalid in new[] { "", "https://database.example.test", "database.example.test:5432", "db & command", " db.example.test" })
            Assert.ThrowsException<ArgumentException>(() => SsmService.CreateStartInfo(context, "i-0123456789abcdef0", 5432, 15432, invalid));
    }

    [TestMethod]
    public void InvalidTunnelBatchRejectedBeforeStartingAnything()
    {
        Assert.ThrowsException<ArgumentException>(() => SsmService.ValidatePorts([(3389, 13389), (22, 13389)]));
        Assert.ThrowsException<ArgumentException>(() => SsmService.ValidatePorts([(3389, 13389), (22, 0)]));
        Assert.ThrowsException<ArgumentException>(() => SsmService.ValidatePorts([]));
    }

    [TestMethod]
    public void DnsAliasPreservesTargetAndDoesNotSendTtl()
    {
        var original = new Amazon.Route53.Model.ResourceRecordSet
        {
            Name = "service.example.test.",
            Type = "A",
            AliasTarget = new Amazon.Route53.Model.AliasTarget { DNSName = "target.example.test.", HostedZoneId = "ZONEDEMO", EvaluateTargetHealth = true }
        };
        var model = new AwsManager.Models.ResourceRecordSetModel { Original = original, Name = original.Name, Type = "A", ResourceRecords = [original.AliasTarget.DNSName] };
        var result = new AwsManager.ViewModels.EditRecordSetViewModel(model).BuildRecord();
        Assert.AreEqual("ZONEDEMO", result.AliasTarget.HostedZoneId);
        Assert.AreEqual(true, result.AliasTarget.EvaluateTargetHealth);
        Assert.IsNull(result.TTL);
        Assert.IsNull(result.ResourceRecords);
        Assert.AreSame(original, model.Original);
    }

    [TestMethod]
    public void AdvancedDnsCannotBeFlattenedByEditor()
    {
        var model = new AwsManager.Models.ResourceRecordSetModel { Original = new Amazon.Route53.Model.ResourceRecordSet { SetIdentifier = "weighted", Weight = 20 } };
        Assert.IsFalse(model.IsEditable);
        Assert.ThrowsException<ArgumentException>(() => new AwsManager.ViewModels.EditRecordSetViewModel(model).BuildRecord());
    }

    [TestMethod]
    public void InvalidDnsAddressRejected()
    {
        var editor = new AwsManager.ViewModels.EditRecordSetViewModel { Name = "service.example.test", Type = "A", Value = "not-an-ip" };
        Assert.ThrowsException<ArgumentException>(() => editor.BuildRecord());
        editor.Value = "192.0.2.1";
        Assert.AreEqual("192.0.2.1", editor.BuildRecord().ResourceRecords[0].Value);
    }
}