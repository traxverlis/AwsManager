using AwsManager.Models;
using AwsManager.Services;
using AwsManager.ViewModels;
using AwsManager.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AwsManager.Tests;

[TestClass]
public class UiSmokeTests
{
    private sealed class OfflineBackend : IAwsSessionBackend
    {
        public bool AllowVerification { get; set; }
        public IReadOnlyList<AwsProfile> ListProfiles() => [new("demo-sso", "eu-west-1", true)];
        public Task LoginAsync(string profile, CancellationToken cancellationToken) => throw new AssertFailedException("Aucun login pendant le rendu.");
        public Task<AwsContext> VerifyAsync(string profile, string region, CancellationToken cancellationToken) => AllowVerification
            ? Task.FromResult(new AwsContext(profile, region, "000000000000", "arn:aws:iam::000000000000:user/Demo", new Amazon.Runtime.AnonymousAWSCredentials()))
            : throw new AssertFailedException("Aucune verification attendue pendant le rendu initial.");
    }

    [TestMethod]
    public void AllScreensRenderOfflineAtDesktopSizes()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                var backend = new OfflineBackend();
                var session = new AwsSessionService(backend);
                var workspacePath = Path.Combine(Path.GetTempPath(), "AwsManager-ui-check", "workspace-" + Guid.NewGuid() + ".json");
                var store = new WorkspaceStore(workspacePath);
                var initialFactory = new Mock<IAwsClientFactory>();
                WorkspaceFeatureTests.SetupPermissions(initialFactory, (_, _) => "implicitDeny");
                var main = new MainViewModel(session, store, () => initialFactory.Object);
                var shell = new MainWindow(main);
                Render(shell, "connexion", 1280, 720);
                CheckHelpUi(main, shell);
                var notification = (FrameworkElement)shell.FindName("NotificationBanner");
                Assert.IsFalse(notification.IsVisible);
                main.Notification = "Inventaire actualisé.";
                Render(shell, "notification", 1280, 720);
                Assert.IsTrue(notification.IsVisible);
                main.DismissNotificationCommand.Execute(null);
                var controls = (FrameworkElement)shell.FindName("ConnectionControls");
                var details = (FrameworkElement)shell.FindName("ConnectionDetails");
                var summary = (FrameworkElement)shell.FindName("ConnectionSummary");
                Assert.IsTrue(controls.IsVisible && details.IsVisible);
                Assert.IsFalse(summary.IsVisible);
                Assert.IsFalse(main.ToggleConnectionPanelCommand.CanExecute(null));
                var expandedHeight = controls.ActualHeight + details.ActualHeight;
                main.SelectedPage = main.Pages.Single(page => page.Name == "Sessions");
                backend.AllowVerification = true;
                session.ConnectAsync("demo-sso", "eu-west-1", false).GetAwaiter().GetResult();
                Render(shell, "connexion-compacte", 1280, 720);
                Assert.IsFalse(main.IsConnectionExpanded);
                Assert.IsTrue(summary.IsVisible);
                Assert.IsFalse(controls.IsVisible || details.IsVisible);
                Assert.IsTrue(summary.ActualHeight < expandedHeight);
                foreach (var text in new[] { "demo-sso", "000000000000", "eu-west-1" })
                    Assert.IsTrue(Descendants<TextBlock>(summary).Any(block => block.IsVisible && block.Text == text));
                main.ToggleConnectionPanelCommand.Execute(null);
                Render(shell, "connexion-deployee", 1000, 640);
                Assert.IsTrue(controls.IsVisible && details.IsVisible);
                Assert.IsFalse(summary.IsVisible);
                main.ToggleConnectionPanelCommand.Execute(null);
                Render(shell, "connexion-compacte-1000", 1000, 640);
                Assert.IsTrue(summary.IsVisible);
                session.ReportError(new Amazon.Runtime.AmazonServiceException("expired") { ErrorCode = "ExpiredToken" }, session.Context);
                Render(shell, "connexion-expiree", 1000, 640);
                Assert.IsTrue(main.IsConnectionExpanded && controls.IsVisible && details.IsVisible);
                Assert.IsFalse(summary.IsVisible);
                session.ConnectAsync("demo-sso", "eu-west-1", false).GetAwaiter().GetResult();
                Assert.IsFalse(main.IsConnectionExpanded);
                main.SelectedRegion = "us-east-1";
                Assert.IsTrue(main.IsConnectionExpanded);
                shell.Close();
                var ec2 = new Ec2ViewModel(null, false);
                ec2.Instances.Add(new Ec2InstanceModel { InstanceId = "i-0123456789abcdef0", Name = "application-demo", State = "running", InstanceType = "t3.medium", PrivateIp = "10.0.0.10", IsSsmManaged = true });
                ec2.Instances.Add(new Ec2InstanceModel { InstanceId = "i-0123456789abcdef1", Name = "worker-demo", State = "stopped", InstanceType = "t3.small", PrivateIp = "10.0.0.20" });
                ec2.Instances.Add(new Ec2InstanceModel { InstanceId = "i-0123456789abcdef2", Name = "batch-demo", State = "pending", InstanceType = "t3.medium", PrivateIp = "10.0.0.30" });
                ec2.SelectedInstance = ec2.Instances[0];
                var s3 = new S3ViewModel(null, false);
                s3.Items.Add(new S3ItemModel { Name = "documents-demo", ItemType = "Bucket" });
                var rds = new RdsViewModel(null, false);
                rds.Instances.Add(new RdsInstanceModel { DbInstanceIdentifier = "database-demo", Engine = "postgres", DbInstanceStatus = "available" });
                rds.Instances.Add(new RdsInstanceModel { DbInstanceIdentifier = "database-recette", Engine = "postgres", DbInstanceStatus = "stopped" });
                var asg = new AutoScalingViewModel(null, false);
                asg.AutoScalingGroups.Add(new AutoScalingGroupModel { AutoScalingGroupName = "application-demo", MinSize = 1, MaxSize = 4, DesiredCapacity = 2 });
                asg.SelectedGroup = asg.AutoScalingGroups[0];
                var security = new SecurityGroupViewModel(null, false);
                security.SecurityGroups.Add(new SecurityGroupModel { GroupId = "sg-0123456789abcdef0", GroupName = "application-demo", Description = "Acces HTTPS" });
                security.SelectedSecurityGroup = security.SecurityGroups[0];
                security.SelectedSecurityGroup.IngressRules.Add(new SecurityGroupRuleModel { RuleId = "sgr-demo", Protocol = "tcp", PortRange = "443", SourceOrDestination = "10.0.0.0/8" });
                var screens = new (UserControl View, object Model)[]
                {
                    (new Ec2View(), ec2), (new S3View(), s3), (new RdsView(), rds),
                    (new AutoScalingView(), asg), (new SecurityGroupView(), security),
                    (new Route53View(), new Route53ViewModel()), (new LiveSessionsView(), new LiveSessionsViewModel())
                };
                foreach (var screen in screens)
                {
                    var window = new Window { Content = screen.View, DataContext = screen.Model, Background = (Brush)app.FindResource("CanvasBrush"), Padding = new Thickness(20) };
                    foreach (var width in new[] { 780, 1060, 1680 }) Render(window, $"{screen.View.GetType().Name}-{width}", width, 600);
                    if (screen.View is AutoScalingView scalingView)
                    {
                        var minimum = (TextBox)scalingView.FindName("MinimumInput");
                        minimum.Text = "invalide";
                        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        Assert.IsTrue(Validation.GetHasError(minimum));
                        var apply = Descendants<Button>(scalingView).Single(button => ReferenceEquals(button.Command, asg.UpdateGroupCommand));
                        Assert.IsFalse(apply.IsEnabled, "Une saisie invalide ne doit pas envoyer l'ancienne capacite.");
                    }
                    if (screen.View is S3View)
                    {
                        var table = Descendants<DataGrid>(screen.View).Single();
                        var trigger = Microsoft.Xaml.Behaviors.Interaction.GetTriggers(table).OfType<Microsoft.Xaml.Behaviors.EventTrigger>().Single();
                        var open = trigger.Actions.OfType<Microsoft.Xaml.Behaviors.InvokeCommandAction>().Single();
                        Assert.AreEqual("MouseDoubleClick", trigger.EventName);
                        Assert.AreSame(s3.OpenItemCommand, open.Command);
                        table.SelectedItem = s3.Items[0];
                        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        Assert.AreSame(s3.Items[0], s3.SelectedFile);
                        object? openedItem = null;
                        open.Command = new RelayCommand(item => openedItem = item);
                        table.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left)
                        {
                            RoutedEvent = Control.MouseDoubleClickEvent
                        });
                        Assert.AreSame(s3.Items[0], openedItem, "Le double-clic doit ouvrir la ressource selectionnee.");
                        var firstFile = new S3ItemModel { Name = "rapport-01.csv", Key = "rapport-01.csv", ItemType = "File" };
                        var secondFile = new S3ItemModel { Name = "rapport-02.csv", Key = "rapport-02.csv", ItemType = "File" };
                        s3.Items.Add(firstFile);
                        s3.Items.Add(secondFile);
                        table.SelectedItems.Clear();
                        table.SelectedItems.Add(firstFile);
                        table.SelectedItems.Add(secondFile);
                        Render(window, "s3-selection-multiple", 1060, 600);
                        Assert.AreEqual(DataGridSelectionMode.Extended, table.SelectionMode);
                        Assert.AreEqual(2, s3.SelectedFileCount);
                        Assert.IsFalse(s3.DownloadFileCommand.CanExecute(null));
                        Assert.IsFalse(s3.PresignFileCommand.CanExecute(null));
                        s3.SearchText = "rapport-01";
                        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        Assert.AreEqual(1, s3.SelectedFileCount);
                    }
                    if (screen.View is Ec2View)
                    {
                        var table = Descendants<DataGrid>(screen.View).Single();
                        var selectedCell = Descendants<DataGridCell>(table).First(cell => cell.IsSelected);
                        Assert.AreEqual(Colors.Transparent, ((SolidColorBrush)selectedCell.Background).Color,
                            $"CellStyle attendu={ReferenceEquals(selectedCell.Style, app.FindResource("ResourceCell"))}; table={ReferenceEquals(table.CellStyle, app.FindResource("ResourceCell"))}; source={DependencyPropertyHelper.GetValueSource(selectedCell, Control.BackgroundProperty).BaseValueSource}; tableStyleSource={DependencyPropertyHelper.GetValueSource(table, DataGrid.CellStyleProperty).BaseValueSource}");
                        var header = Descendants<System.Windows.Controls.Primitives.DataGridColumnHeader>(table).First(column => column.Column == table.Columns[0]);
                        Assert.IsNotNull(header.Template.FindName("PART_RightHeaderGripper", header));
                        var click = typeof(System.Windows.Controls.Primitives.DataGridColumnHeader).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        Assert.IsNotNull(click);
                        click.Invoke(header, null);
                        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        Assert.AreEqual(System.ComponentModel.ListSortDirection.Ascending, table.Columns[0].SortDirection);
                        Assert.AreEqual("application-demo", ((Ec2InstanceModel)table.Items[0]).Name);
                        ec2.SearchText = "aucune-correspondance";
                        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        Assert.IsTrue(Descendants<TextBlock>(screen.View).Any(text => text.IsVisible && text.Text == "Aucun résultat"));
                    }
                    window.Close();
                }
                store.SetReadOnly(false);
                var bulkClient = new Mock<Amazon.S3.IAmazonS3>();
                bulkClient.Setup(client => client.GetBucketLocationAsync(It.IsAny<Amazon.S3.Model.GetBucketLocationRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.S3.Model.GetBucketLocationResponse());
                bulkClient.Setup(client => client.ListObjectsV2Async(It.IsAny<Amazon.S3.Model.ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new Amazon.S3.Model.ListObjectsV2Response { S3Objects = [new() { Key = "rapport-01.csv" }, new() { Key = "rapport-02.csv" }] });
                bulkClient.Setup(client => client.DeleteObjectsAsync(It.IsAny<Amazon.S3.Model.DeleteObjectsRequest>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new Amazon.S3.Model.DeleteObjectsResponse { DeletedObjects = [new() { Key = "rapport-01.csv" }], DeleteErrors = [new() { Key = "rapport-02.csv", Code = "AccessDenied" }] });
                var bulkFactory = new Mock<IAwsClientFactory>();
                bulkFactory.Setup(factory => factory.CreateS3Client(It.IsAny<string?>())).Returns(bulkClient.Object);
                var deletionConfirmations = 0;
                var bulkModel = new S3ViewModel(bulkFactory.Object, false, _ => { deletionConfirmations++; return true; });
                ((AsyncRelayCommand)bulkModel.OpenItemCommand).ExecuteAsync(new S3ItemModel { Name = "documents-demo", ItemType = "Bucket" }).GetAwaiter().GetResult();
                var bulkView = new S3View();
                var bulkWindow = new Window { Content = bulkView, DataContext = bulkModel };
                Render(bulkWindow, "s3-lot-initial", 1060, 600);
                var bulkTable = Descendants<DataGrid>(bulkView).Single();
                foreach (var file in bulkModel.Items.Where(item => item.ItemType == "File").ToArray()) bulkTable.SelectedItems.Add(file);
                bulkWindow.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var deleteFiles = Descendants<Button>(bulkView).Single(button => ReferenceEquals(button.Command, bulkModel.DeleteFileCommand));
                Assert.IsTrue(deleteFiles.IsEnabled);
                Render(bulkWindow, "s3-selection-multiple", 1060, 600);
                ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(deleteFiles)).Invoke();
                bulkWindow.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.AreEqual(1, deletionConfirmations);
                Assert.AreEqual(1, bulkModel.SelectedFileCount);
                Assert.AreEqual("rapport-02.csv", bulkTable.SelectedItems.OfType<S3ItemModel>().Single().Key);
                StringAssert.Contains(bulkModel.Status, "1/2");
                Render(bulkWindow, "s3-suppression-partielle", 780, 600);
                ((AsyncRelayCommand)bulkModel.RefreshCommand).ExecuteAsync(null).GetAwaiter().GetResult();
                Assert.AreEqual(0, bulkModel.SelectedFileCount);
                Assert.IsFalse(bulkModel.DeleteFileCommand.CanExecute(null));
                bulkWindow.Close();
                var connectionContext = new AwsContext("demo-sso", "eu-west-1", "000000000000", "fake", new Amazon.Runtime.AnonymousAWSCredentials());
                var connection = new SsmConnectionViewModel(new Ec2InstanceModel { InstanceId = "i-0123456789abcdef0", Name = "application-demo", Platform = "windows" }, store, connectionContext);
                var dialog = new AwsManager.Views.Dialogs.SsmConnectionWindow { DataContext = connection };
                Render(dialog, "connexion-ssm", 640, 460);
                Descendants<TabControl>(dialog).Single().SelectedIndex = 1;
                Render(dialog, "connexion-rdp", 640, 460);
                dialog.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                ((TextBox)dialog.FindName("RdpRemoteInput")).Text = "invalide";
                ((AsyncRelayCommand)connection.StartRdpTunnelCommand).ExecuteAsync(dialog.FindName("RdpForm")).GetAwaiter().GetResult();
                StringAssert.Contains(connection.Status, "Corrigez les ports invalides");
                Assert.AreEqual(0, SessionTrackingService.Instance.ActiveSessions.Count);
                connection.PresetName = "Bureau recette";
                dialog.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var savePreset = Descendants<Button>(dialog).Single(button => ReferenceEquals(button.Command, connection.SavePresetCommand));
                ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(savePreset)).Invoke();
                dialog.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.AreEqual(0, store.Snapshot.Connections.Length, "Un port invalide ne doit pas enregistrer son ancienne valeur.");
                ((TextBox)dialog.FindName("RdpRemoteInput")).Text = "3389";
                ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(savePreset)).Invoke();
                dialog.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.AreEqual("Bureau recette", store.Snapshot.Connections.Single().Name);
                Assert.AreEqual("RDP", store.Snapshot.Connections.Single().Mode);
                Render(dialog, "connexion-enregistree", 640, 580);
                dialog.Close();
                var dns = new AwsManager.Views.Dialogs.EditRecordSetWindow { DataContext = new EditRecordSetViewModel { Name = "service.example.test", Type = "A", Value = "192.0.2.10" } };
                Render(dns, "formulaire-dns", 620, 680);
                dns.Close();
                var ruleEditor = new SecurityRuleEditorViewModel(security.SelectedSecurityGroup, security.SecurityGroups);
                var rule = new AwsManager.Views.Dialogs.AddSecurityGroupRuleWindow(ruleEditor);
                Render(rule, "formulaire-securite", 660, 760);
                Assert.IsFalse(((Button)rule.FindName("ContinueButton")).IsEnabled);
                ((ComboBox)rule.FindName("PresetInput")).SelectedItem = ruleEditor.Presets.Single(preset => preset.Name == "HTTPS");
                ((TextBox)rule.FindName("SourceInput")).Text = "10.0.0.0/8";
                rule.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.IsTrue(((Button)rule.FindName("ContinueButton")).IsEnabled);
                Assert.AreEqual(443, ruleEditor.BuildRule().FromPort);
                ((TextBox)rule.FindName("FromInput")).Text = "invalide";
                rule.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.IsFalse(((Button)rule.FindName("ContinueButton")).IsEnabled);
                ((ComboBox)rule.FindName("PresetInput")).SelectedItem = ruleEditor.Presets.Single(preset => preset.Name == "Oracle");
                ((ComboBox)rule.FindName("SourceKindInput")).SelectedValue = "group";
                ((ComboBox)rule.FindName("GroupInput")).SelectedItem = ruleEditor.KnownGroups.Single();
                rule.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.AreEqual(security.SelectedSecurityGroup.GroupId, ruleEditor.Source);
                Assert.IsTrue(ruleEditor.CanSubmit);
                Render(rule, "regle-oracle-groupe", 660, 760);
                ((ComboBox)rule.FindName("SourceKindInput")).SelectedValue = "any4";
                Render(rule, "regle-exposition-publique", 540, 600);
                Assert.IsTrue(Descendants<TextBlock>(rule).Any(text => text.IsVisible && text.Text == ruleEditor.ExposureWarning && text.Text.Length > 0));
                rule.Close();
                var editRule = new SecurityRuleEditorViewModel(security.SelectedSecurityGroup, security.SecurityGroups, new SecurityGroupRuleModel
                { RuleId = "sgr-demo", GroupId = security.SelectedSecurityGroup.GroupId, Type = "Egress", Protocol = "icmpv6", PortRange = "128/0", SourceOrDestination = "2001:db8::/64", Description = "Ping IPv6" });
                var editRuleWindow = new AwsManager.Views.Dialogs.AddSecurityGroupRuleWindow(editRule);
                Render(editRuleWindow, "regle-edition-icmp", 660, 760);
                Assert.IsFalse(((RadioButton)editRuleWindow.FindName("EgressInput")).IsEnabled);
                Assert.AreEqual("128", ((TextBox)editRuleWindow.FindName("FromInput")).Text);
                editRuleWindow.Close();
                var tags = new System.Collections.ObjectModel.ObservableCollection<TagModel>
                {
                    new() { Key = "Environment", Value = "recette" },
                    new() { Key = "Application", Value = "aws-manager" }
                };
                var saved = false;
                var addTag = new RelayCommand(_ => tags.Add(new TagModel { Key = "Owner", Value = "equipe-demo" }));
                var removeTag = new RelayCommand(item => tags.Remove((TagModel)item!));
                var saveTags = new RelayCommand(_ => saved = true);
                var tagDialog = new AwsManager.Views.Dialogs.TagEditorWindow
                {
                    DataContext = new { Tags = tags, InstanceId = "i-0123456789abcdef0", CanEdit = true, Status = "", AddTagCommand = addTag, RemoveTagCommand = removeTag, SaveChangesCommand = saveTags }
                };
                Render(tagDialog, "formulaire-tags", 680, 500);
                var tagTable = Descendants<DataGrid>(tagDialog).Single();
                EditCell(tagTable, tags[0], 1, "production");
                Assert.AreEqual("production", tags[0].Value);
                var addButton = Descendants<Button>(tagDialog).Single(button => ReferenceEquals(button.Command, addTag));
                ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(addButton)).Invoke();
                tagDialog.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.AreEqual(3, tags.Count);
                var removeButton = Descendants<Button>(tagTable).First(button => ReferenceEquals(button.Command, removeTag));
                ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(removeButton)).Invoke();
                tagDialog.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.AreEqual(2, tags.Count);
                var saveButton = Descendants<Button>(tagDialog).Single(button => ReferenceEquals(button.Command, saveTags));
                ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(saveButton)).Invoke();
                tagDialog.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.IsTrue(saved);
                tagDialog.Close();
                var asgTags = new[] { new ASGTagModel { Key = "Environment", Value = "recette", PropageAtLaunch = true } };
                var asgTagDialog = new AwsManager.Views.Dialogs.ASGTagEditorWindow
                {
                    DataContext = new { Tags = asgTags, AutoScalingGroupName = "application-demo", CanEdit = true, Status = "", AddTagCommand = addTag, RemoveTagCommand = removeTag, SaveChangesCommand = saveTags }
                };
                Render(asgTagDialog, "formulaire-tags-asg", 720, 500);
                EditCell(Descendants<DataGrid>(asgTagDialog).Single(), asgTags[0], 1, "production");
                Assert.AreEqual("production", asgTags[0].Value);
                asgTagDialog.Close();
                var resource = new ResourceReference("demo-sso", "000000000000", "eu-west-1", "EC2", "i-demo", "application-demo");
                store.ToggleFavorite(resource);
                store.Visit(resource);
                store.SaveConnection(new(Guid.NewGuid(), "Base recette", resource, "Tunnels", [new(5432, 15432)]));
                store.Record(new(DateTimeOffset.UtcNow, resource.Profile, resource.Account, resource.Region, "EC2", "StopInstances", resource.Id, "Bloquee (lecture seule)"));
                using var workspace = new WorkspaceViewModel(store, _ => Task.CompletedTask, _ => Task.CompletedTask);
                var accessWindow = new Window { Content = new WorkspaceView(), DataContext = workspace };
                foreach (var width in new[] { 780, 1060, 1680 }) Render(accessWindow, $"mes-acces-{width}", width, 600);
                var accessTabs = Descendants<TabControl>(accessWindow).Single();
                for (var tab = 1; tab < accessTabs.Items.Count; tab++)
                {
                    accessTabs.SelectedIndex = tab;
                    Render(accessWindow, $"mes-acces-onglet-{tab}", 1060, 600);
                }
                accessWindow.Close();
                var metricsClient = new Mock<Amazon.CloudWatch.IAmazonCloudWatch>();
                var now = DateTime.UtcNow;
                metricsClient.Setup(client => client.GetMetricDataAsync(It.IsAny<Amazon.CloudWatch.Model.GetMetricDataRequest>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new Amazon.CloudWatch.Model.GetMetricDataResponse { MetricDataResults =
                    [
                        new() { Id = "cpu", StatusCode = Amazon.CloudWatch.StatusCode.Complete, Timestamps = Enumerable.Range(1, 12).Select(index => now.AddMinutes(-60 + index * 5)).ToList(), Values = [12, 20, 18, 35, 40, 30, 55, 42, 35, 60, 45, 32] },
                        new() { Id = "secondary", StatusCode = Amazon.CloudWatch.StatusCode.Complete, Timestamps = Enumerable.Range(1, 12).Select(index => now.AddMinutes(-60 + index * 5)).ToList(), Values = [1000000, 2000000, 1500000, 3000000, 2800000, 3500000, 2300000, 1800000, 2500000, 3200000, 2200000, 1200000] }
                    ] });
                metricsClient.Setup(client => client.DescribeAlarmsAsync(It.IsAny<Amazon.CloudWatch.Model.DescribeAlarmsRequest>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new Amazon.CloudWatch.Model.DescribeAlarmsResponse { MetricAlarms = [new() { AlarmName = "cpu-application-demo", Namespace = "AWS/EC2", Dimensions = [new() { Name = "InstanceId", Value = resource.Id }], StateValue = Amazon.CloudWatch.StateValue.OK, StateUpdatedTimestamp = now }] });
                var relationClient = new Mock<Amazon.EC2.IAmazonEC2>();
                relationClient.Setup(client => client.DescribeInstancesAsync(It.IsAny<Amazon.EC2.Model.DescribeInstancesRequest>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new Amazon.EC2.Model.DescribeInstancesResponse { Reservations = [new() { Instances = [new() { SecurityGroups = [new() { GroupId = "sg-demo", GroupName = "application-demo" }] }] }] });
                var inspectorFactory = new Mock<IAwsClientFactory>();
                inspectorFactory.Setup(factory => factory.CreateCloudWatchClient()).Returns(metricsClient.Object);
                inspectorFactory.Setup(factory => factory.CreateEc2Client()).Returns(relationClient.Object);
                var inspector = new ResourceInspectorViewModel(resource, inspectorFactory.Object, _ => Task.CompletedTask, true);
                var inspectorWindow = new AwsManager.Views.Dialogs.ResourceInspectorWindow { DataContext = inspector };
                Render(inspectorWindow, "cloudwatch-980", 980, 700);
                Assert.AreEqual(2, inspector.Charts.Count);
                Assert.AreEqual(1, inspector.Alarms.Count);
                Assert.AreEqual(2, Descendants<OxyPlot.Wpf.PlotView>(inspectorWindow).Count());
                Render(inspectorWindow, "cloudwatch-740", 740, 640);
                var alarmTable = Descendants<DataGrid>(inspectorWindow).Single(table => ReferenceEquals(table.ItemsSource, inspector.Alarms));
                var alarmRow = Descendants<DataGridRow>(alarmTable).First();
                Assert.IsTrue(alarmRow.TransformToAncestor(alarmTable).Transform(new Point(0, alarmRow.ActualHeight)).Y <= alarmTable.ActualHeight, "La premiere alarme doit rester visible dans la petite fenetre.");
                inspector.SelectedTab = 0;
                Render(inspectorWindow, "ressources-liees", 980, 700);
                Assert.AreEqual("sg-demo", inspector.Related.Single().Id);
                inspectorWindow.Close();
                var rdsBackend = new Mock<IRdsTunnelBackend>();
                var rdsTarget = new RdsTunnelTarget("database-demo", "oracle.demo.eu-west-1.rds.amazonaws.com", 1521, "available", "vpc-demo");
                var rdsRelay = new SsmRelay("i-0123456789abcdef0", "relais-demo", "vpc-demo", "3.3.0.0");
                rdsBackend.Setup(service => service.LoadTargetAsync(rdsTarget.Id, It.IsAny<CancellationToken>())).ReturnsAsync(rdsTarget);
                rdsBackend.Setup(service => service.LoadRelaysAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { rdsRelay });
                var rdsConnection = new RdsTunnelViewModel(new RdsInstanceModel { DbInstanceIdentifier = rdsTarget.Id }, store: store, context: connectionContext, backend: rdsBackend.Object);
                var rdsWindow = new AwsManager.Views.Dialogs.RdsTunnelWindow { DataContext = rdsConnection };
                Render(rdsWindow, "connexion-rds", 680, 740);
                Assert.AreEqual(11521, rdsConnection.LocalPort);
                rdsConnection.SelectedRelay = rdsRelay;
                rdsConnection.PresetName = "Oracle recette";
                var countBeforeSave = store.Snapshot.Connections.Length;
                ((TextBox)rdsWindow.FindName("LocalPortInput")).Text = "invalide";
                awaitOnDispatcher(rdsWindow, () => ((AsyncRelayCommand)rdsConnection.SavePresetCommand).ExecuteAsync(rdsWindow.FindName("ConnectionForm")));
                Assert.AreEqual(countBeforeSave, store.Snapshot.Connections.Length);
                awaitOnDispatcher(rdsWindow, () => ((AsyncRelayCommand)rdsConnection.OpenCommand).ExecuteAsync(rdsWindow.FindName("ConnectionForm")));
                rdsBackend.Verify(service => service.OpenAsync(It.IsAny<RdsTunnelTarget>(), It.IsAny<SsmRelay>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
                ((TextBox)rdsWindow.FindName("LocalPortInput")).Text = "11521";
                awaitOnDispatcher(rdsWindow, () => ((AsyncRelayCommand)rdsConnection.SavePresetCommand).ExecuteAsync(rdsWindow.FindName("ConnectionForm")));
                Assert.AreEqual("RDS", store.Snapshot.Connections.Last().Mode);
                Assert.AreEqual(rdsRelay.Id, store.Snapshot.Connections.Last().RelayInstanceId);
                Render(rdsWindow, "connexion-rds-enregistree", 680, 740);
                rdsWindow.Close();
                var logsClient = new Mock<Amazon.CloudWatchLogs.IAmazonCloudWatchLogs>();
                logsClient.Setup(client => client.DescribeLogGroupsAsync(It.IsAny<Amazon.CloudWatchLogs.Model.DescribeLogGroupsRequest>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new Amazon.CloudWatchLogs.Model.DescribeLogGroupsResponse { LogGroups = [new() { LogGroupName = "/aws/rds/instance/database-demo/postgresql", RetentionInDays = 7 }, new() { LogGroupName = "/application/recette", RetentionInDays = 30 }] });
                logsClient.Setup(client => client.FilterLogEventsAsync(It.IsAny<Amazon.CloudWatchLogs.Model.FilterLogEventsRequest>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new Amazon.CloudWatchLogs.Model.FilterLogEventsResponse { Events = Enumerable.Range(0, 8).Select(index => new Amazon.CloudWatchLogs.Model.FilteredLogEvent
                    { EventId = index.ToString(), Timestamp = DateTimeOffset.UtcNow.AddMinutes(-index).ToUnixTimeMilliseconds(), LogStreamName = "database-demo.2026-09-18", Message = $"ERROR Test {index}\nDiagnostic de demonstration, sans donnees reelles.\nDetail multilignes du journal." }).ToList() });
                var logsFactory = new Mock<IAwsClientFactory>();
                logsFactory.Setup(factory => factory.CreateCloudWatchLogsClient()).Returns(logsClient.Object);
                using var logs = new CloudWatchLogsViewModel(logsFactory.Object);
                logs.RefreshAsync().GetAwaiter().GetResult();
                logs.SelectedGroup = logs.Groups[0];
                logs.Pattern = "ERROR";
                logs.SearchAsync().GetAwaiter().GetResult();
                logs.SelectedEvent = logs.Events[0];
                var logsView = new CloudWatchLogsView();
                var logsWindow = new Window { Content = logsView, DataContext = logs };
                foreach (var width in new[] { 780, 1060, 1680 }) Render(logsWindow, $"logs-{width}", width, 600);
                Assert.AreEqual(logs.SelectedEvent.Message, ((TextBox)logsView.FindName("MessageDetail")).Text);
                Render(logsWindow, "logs-compact", 780, 480);
                var messageDetail = (FrameworkElement)logsView.FindName("MessageDetail");
                Assert.IsTrue(messageDetail.ActualHeight >= 40);
                Assert.IsTrue(messageDetail.TransformToAncestor(logsView).Transform(new Point(0, messageDetail.ActualHeight)).Y <= logsView.ActualHeight);
                logsWindow.Close();
                var logsMain = new MainViewModel(session, store, () => logsFactory.Object);
                var logsPermissions = WorkspaceFeatureTests.SetupPermissions(logsFactory);
                logsMain.SelectedPage = logsMain.Pages.Single(page => page.Name == "Logs");
                awaitOnDispatcher(logsWindow, async () => { await session.ConnectAsync("demo-sso", "eu-west-1", false); await logsMain.PermissionsReady; }, () => $"{logsMain.PermissionStatus}; appels={logsPermissions.Invocations.Count}");
                var currentLogs = (CloudWatchLogsViewModel)logsMain.CurrentViewModel!;
                currentLogs.SelectedGroup = currentLogs.Groups[0];
                awaitOnDispatcher(logsWindow, () => currentLogs.SearchAsync());
                currentLogs.SelectedEvent = currentLogs.Events[0];
                var logsShell = new MainWindow(logsMain);
                Render(logsShell, "logs-shell-compact", 1000, 640);
                logsMain.SelectedRegion = "us-east-1";
                Assert.AreEqual(0, currentLogs.Events.Count);
                Assert.IsNull(currentLogs.SelectedEvent);
                Assert.IsFalse(currentLogs.SearchCommand.CanExecute(null));
                logsShell.Close();
                CheckAdministrationUi(store, connectionContext);
                CheckPermissionUi(store);
                File.Delete(workspacePath);
                app.Shutdown();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(60)), "Le rendu WPF doit terminer.");
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }

    private static void CheckHelpUi(MainViewModel main, Window shell)
    {
        var helpButton = Descendants<Button>(shell).Single(button => ReferenceEquals(button.Command, main.HelpCommand));
        ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(helpButton)).Invoke();
        shell.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var help = Application.Current.Windows.OfType<AwsManager.Views.Dialogs.HelpWindow>().Single();
        Assert.AreSame(shell, help.Owner);
        var model = (HelpViewModel)help.DataContext;
        var topics = (ListBox)help.FindName("HelpTopics");
        var search = (TextBox)help.FindName("HelpSearch");
        var scroll = (ScrollViewer)help.FindName("TopicScroll");
        Assert.AreEqual(7, model.Topics.Count);
        foreach (var size in new[] { (980, 700), (760, 560), (1200, 800) })
            Render(help, $"aide-{size.Item1}", size.Item1, size.Item2);
        foreach (var topic in model.Topics)
        {
            topics.SelectedItem = topic;
            help.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.AreEqual(topic.Title, ((TextBlock)help.FindName("TopicTitle")).Text);
            Assert.AreEqual(0, scroll.VerticalOffset);
            scroll.ScrollToEnd();
            help.UpdateLayout();
        }
        search.Text = "  SESSION EXPIREE  ";
        help.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.AreEqual(1, model.Topics.Count);
        Assert.AreEqual("D\u00e9pannage", model.SelectedTopic!.Title);
        Render(help, "aide-recherche", 760, 560);
        search.Text = "iam:SimulatePrincipalPolicy";
        help.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.AreEqual("Droits et s\u00e9curit\u00e9", model.SelectedTopic!.Title);
        Render(help, "aide-droits", 980, 700);
        search.Text = "aucune-rubrique-correspondante";
        Render(help, "aide-vide", 760, 560);
        Assert.IsNull(model.SelectedTopic);
        Assert.IsTrue(((FrameworkElement)help.FindName("HelpEmptyState")).IsVisible);
        ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer((Button)help.FindName("ClearHelpSearch"))).Invoke();
        help.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.AreEqual(7, model.Topics.Count);
        Assert.IsTrue(model.HasSelection);
        Assert.AreEqual("", search.Text);
        Assert.IsFalse(((FrameworkElement)help.FindName("HelpEmptyState")).IsVisible);
        var close = (Button)help.FindName("CloseHelpButton");
        Assert.IsTrue(close.TransformToAncestor(help).Transform(new Point(close.ActualWidth, close.ActualHeight)).Y <= help.ActualHeight);
        ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(close)).Invoke();
        help.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.IsFalse(help.IsVisible);
    }

    private static void CheckAdministrationUi(WorkspaceStore store, AwsContext context)
    {
        const string roleArn = "arn:aws:iam::000000000000:role/role-application";
        const string profileArn = "arn:aws:iam::000000000000:instance-profile/profil-application";
        var role = new Amazon.IdentityManagement.Model.Role { RoleName = "role-application", Arn = roleArn, Path = "/", AssumeRolePolicyDocument = "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":{\"Service\":\"ec2.amazonaws.com\"},\"Action\":\"sts:AssumeRole\"}]}" };
        var instanceProfile = new Amazon.IdentityManagement.Model.InstanceProfile { InstanceProfileName = "profil-application", Arn = profileArn, Path = "/", Roles = [role] };
        var iam = new Mock<Amazon.IdentityManagement.IAmazonIdentityManagementService>();
        iam.Setup(client => client.ListRolesAsync(It.IsAny<Amazon.IdentityManagement.Model.ListRolesRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.IdentityManagement.Model.ListRolesResponse { Roles = [role] });
        iam.Setup(client => client.GetRoleAsync(It.IsAny<Amazon.IdentityManagement.Model.GetRoleRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.IdentityManagement.Model.GetRoleResponse { Role = role });
        iam.Setup(client => client.ListInstanceProfilesForRoleAsync(It.IsAny<Amazon.IdentityManagement.Model.ListInstanceProfilesForRoleRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.IdentityManagement.Model.ListInstanceProfilesForRoleResponse { InstanceProfiles = [instanceProfile] });
        iam.Setup(client => client.ListAttachedRolePoliciesAsync(It.IsAny<Amazon.IdentityManagement.Model.ListAttachedRolePoliciesRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.IdentityManagement.Model.ListAttachedRolePoliciesResponse { AttachedPolicies = [new() { PolicyName = "AmazonSSMManagedInstanceCore", PolicyArn = "arn:aws:iam::aws:policy/AmazonSSMManagedInstanceCore" }] });
        iam.Setup(client => client.ListRolePoliciesAsync(It.IsAny<Amazon.IdentityManagement.Model.ListRolePoliciesRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.IdentityManagement.Model.ListRolePoliciesResponse { PolicyNames = ["lecture-documents"] });
        iam.Setup(client => client.GetRolePolicyAsync(It.IsAny<Amazon.IdentityManagement.Model.GetRolePolicyRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.IdentityManagement.Model.GetRolePolicyResponse { PolicyDocument = "%7B%22Version%22%3A%222012-10-17%22%2C%22Statement%22%3A%5B%5D%7D" });
        iam.Setup(client => client.ListInstanceProfilesAsync(It.IsAny<Amazon.IdentityManagement.Model.ListInstanceProfilesRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.IdentityManagement.Model.ListInstanceProfilesResponse { InstanceProfiles = [instanceProfile] });
        iam.Setup(client => client.GetInstanceProfileAsync(It.IsAny<Amazon.IdentityManagement.Model.GetInstanceProfileRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.IdentityManagement.Model.GetInstanceProfileResponse { InstanceProfile = instanceProfile });
        iam.Setup(client => client.ListPoliciesAsync(It.IsAny<Amazon.IdentityManagement.Model.ListPoliciesRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.IdentityManagement.Model.ListPoliciesResponse { Policies = [new() { PolicyName = "AmazonSSMManagedInstanceCore", Arn = "arn:aws:iam::aws:policy/AmazonSSMManagedInstanceCore", Path = "/" }] });
        iam.Setup(client => client.ListUsersAsync(It.IsAny<Amazon.IdentityManagement.Model.ListUsersRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.IdentityManagement.Model.ListUsersResponse { Users = [new() { UserName = "utilisateur-demo", Arn = "arn:aws:iam::000000000000:user/utilisateur-demo", Path = "/" }] });
        iam.Setup(client => client.ListGroupsAsync(It.IsAny<Amazon.IdentityManagement.Model.ListGroupsRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.IdentityManagement.Model.ListGroupsResponse { Groups = [new() { GroupName = "lecture-demo", Arn = "arn:aws:iam::000000000000:group/lecture-demo", Path = "/" }] });
        var ec2 = new Mock<Amazon.EC2.IAmazonEC2>();
        ec2.Setup(client => client.DescribeInstancesAsync(It.IsAny<Amazon.EC2.Model.DescribeInstancesRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.EC2.Model.DescribeInstancesResponse { Reservations = [new() { Instances = [new() { InstanceId = "i-0123456789abcdef0", State = new() { Name = "running" } }] }] });
        ec2.Setup(client => client.DescribeIamInstanceProfileAssociationsAsync(It.IsAny<Amazon.EC2.Model.DescribeIamInstanceProfileAssociationsRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.EC2.Model.DescribeIamInstanceProfileAssociationsResponse { IamInstanceProfileAssociations = [new() { AssociationId = "iip-assoc-demo", State = "associated", IamInstanceProfile = new() { Arn = "arn:aws:iam::000000000000:instance-profile/ancien" } }] });
        ec2.Setup(client => client.ReplaceIamInstanceProfileAssociationAsync(It.IsAny<Amazon.EC2.Model.ReplaceIamInstanceProfileAssociationRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.EC2.Model.ReplaceIamInstanceProfileAssociationResponse { IamInstanceProfileAssociation = new() { State = "associating" } });
        var (_, ssm) = PatchWorkflowTests.Setup();
        const string commandId = "00000000-0000-4000-8000-000000000001";
        ssm.Setup(client => client.ListCommandsAsync(It.IsAny<Amazon.SimpleSystemsManagement.Model.ListCommandsRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.SimpleSystemsManagement.Model.ListCommandsResponse { Commands = [new() { CommandId = commandId, DocumentName = PatchService.DocumentName, StatusDetails = "Success", RequestedDateTime = DateTime.UtcNow, TargetCount = 1, CompletedCount = 1, ErrorCount = 0, Parameters = new() { ["Operation"] = ["Install"], ["RebootOption"] = ["NoReboot"] } }] });
        ssm.Setup(client => client.ListCommandInvocationsAsync(It.IsAny<Amazon.SimpleSystemsManagement.Model.ListCommandInvocationsRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Amazon.SimpleSystemsManagement.Model.ListCommandInvocationsResponse { CommandInvocations = [new() { CommandId = commandId, DocumentName = PatchService.DocumentName, InstanceId = "mi-0123456789abcdef0", InstanceName = "recette", StatusDetails = "Success", CommandPlugins = [new() { Name = "PatchWindows", StatusDetails = "Success", ResponseCode = 0, Output = "Resultat simule : 3 correctifs installes, redemarrage en attente." }] }] });
        var factory = new Mock<IAwsClientFactory>();
        factory.Setup(client => client.CreateIamClient()).Returns(iam.Object);
        WorkspaceFeatureTests.SetupPermissions(factory, client: iam);
        factory.Setup(client => client.CreateEc2Client()).Returns(ec2.Object);
        factory.Setup(client => client.CreateSsmClient()).Returns(ssm.Object);
        using var iamModel = new IamViewModel(factory.Object, context);
        iamModel.RefreshAsync().GetAwaiter().GetResult();
        var iamView = new IamView();
        var iamWindow = new Window { Content = iamView, DataContext = iamModel, Padding = new Thickness(16) };
        Render(iamWindow, "iam-roles", 1060, 650);
        Descendants<DataGrid>(iamView).Single().SelectedItem = iamModel.Items[0];
        iamWindow.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        StringAssert.Contains(iamModel.Details, "ec2.amazonaws.com");
        StringAssert.Contains(iamModel.Details, "lecture-documents");
        Render(iamWindow, "iam-role-detail", 780, 600);
        var categories = (TabControl)iamView.FindName("IamCategories");
        for (var category = 1; category < 5; category++)
        {
            categories.SelectedIndex = category;
            iamWindow.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.AreEqual(1, iamModel.Items.Count);
            Assert.AreEqual(iamModel.Categories[category].Id, iamModel.Items[0].Kind);
        }
        iamWindow.Close();
        var profileConfirmation = false;
        using var profileModel = new Ec2ProfileViewModel("i-0123456789abcdef0", factory.Object, context, _ => profileConfirmation);
        var profileWindow = new AwsManager.Views.Dialogs.Ec2ProfileWindow(profileModel);
        Render(profileWindow, "profil-ec2-initial", 700, 570);
        ((ComboBox)profileWindow.FindName("ProfileInput")).SelectedIndex = 0;
        profileWindow.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.IsTrue(profileModel.CanApply);
        awaitOnDispatcher(profileWindow, () => ((AsyncRelayCommand)profileModel.ApplyCommand).ExecuteAsync(null));
        ec2.Verify(client => client.ReplaceIamInstanceProfileAssociationAsync(It.IsAny<Amazon.EC2.Model.ReplaceIamInstanceProfileAssociationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        profileConfirmation = true;
        Render(profileWindow, "profil-ec2-selection", 560, 520);
        ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer((Button)profileWindow.FindName("ApplyProfileButton"))).Invoke();
        profileWindow.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        ec2.Verify(client => client.ReplaceIamInstanceProfileAssociationAsync(It.Is<Amazon.EC2.Model.ReplaceIamInstanceProfileAssociationRequest>(request => request.IamInstanceProfile.Arn == profileArn), It.IsAny<CancellationToken>()), Times.Once);
        Assert.IsFalse(profileModel.CanApply);
        profileWindow.Close();
        store.SetReadOnly(false);
        using var patchModel = new PatchViewModel(factory.Object, store, context, _ => true);
        patchModel.RefreshAsync().GetAwaiter().GetResult();
        var patchView = new PatchView();
        var patchWindow = new Window { Content = patchView, DataContext = patchModel, Padding = new Thickness(16) };
        Render(patchWindow, "correctifs-parc", 1060, 650);
        var table = (DataGrid)patchView.FindName("NodesTable");
        table.SelectedItems.Add(patchModel.Nodes[0]);
        ((ComboBox)patchView.FindName("OperationInput")).SelectedValue = "Install";
        patchWindow.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.AreEqual(1, patchModel.SelectionCount);
        Assert.IsTrue(patchModel.CanPrepare);
        awaitOnDispatcher(patchWindow, () => ((AsyncRelayCommand)patchModel.PrepareCommand).ExecuteAsync(null));
        Assert.AreEqual(1, patchModel.SelectedTab);
        Assert.IsFalse(patchModel.CanSubmit);
        ((CheckBox)patchView.FindName("MaintenanceAcknowledged")).IsChecked = true;
        patchWindow.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.IsTrue(patchModel.CanSubmit);
        store.SetReadOnly(true);
        Assert.IsFalse(patchModel.CanSubmit);
        store.SetReadOnly(false);
        Render(patchWindow, "correctifs-preparation", 780, 600);
        var submit = (Button)patchView.FindName("SubmitPatchButton");
        Assert.IsTrue(submit.TransformToAncestor(patchView).Transform(new Point(0, submit.ActualHeight)).Y <= patchView.ActualHeight);
        ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(submit)).Invoke();
        patchWindow.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        ssm.Verify(client => client.SendCommandAsync(It.IsAny<Amazon.SimpleSystemsManagement.Model.SendCommandRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.AreEqual(2, patchModel.SelectedTab);
        Assert.AreEqual(commandId, patchModel.SelectedCommand!.Id);
        Assert.IsFalse(patchModel.CanSubmit);
        awaitOnDispatcher(patchWindow, () => ((AsyncRelayCommand)patchModel.RefreshInvocationsCommand).ExecuteAsync(null));
        patchModel.SelectedInvocation = patchModel.Invocations.Single();
        StringAssert.Contains(patchModel.CommandOutput, "Resultat simule");
        Render(patchWindow, "correctifs-executions", 1060, 650);
        patchWindow.Close();
        var backend = new OfflineBackend { AllowVerification = true };
        var session = new AwsSessionService(backend);
        var main = new MainViewModel(session, store, () => factory.Object);
        main.SelectedPage = main.Pages.Single(page => page.Name == "IAM");
        var shell = new MainWindow(main);
        awaitOnDispatcher(shell, async () => { await session.ConnectAsync("demo-sso", "eu-west-1", false); await main.PermissionsReady; });
        Render(shell, "iam-shell-compact", 1000, 640);
        main.SelectedPage = main.Pages.Single(page => page.Name == "Correctifs SSM");
        var activePatch = (PatchViewModel)main.CurrentViewModel!;
        Render(shell, "correctifs-shell-compact", 1000, 640);
        main.SelectedRegion = "us-east-1";
        Assert.AreEqual(0, activePatch.Nodes.Count);
        Assert.IsFalse(activePatch.CanSubmit);
        shell.Close();
        ssm.Verify(client => client.CancelCommandAsync(It.IsAny<Amazon.SimpleSystemsManagement.Model.CancelCommandRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static void CheckPermissionUi(WorkspaceStore store)
    {
        var session = new AwsSessionService(new OfflineBackend { AllowVerification = true });
        var factory = new Mock<IAwsClientFactory>();
        var iam = WorkspaceFeatureTests.SetupPermissions(factory, (action, _) => action is "ec2:DescribeInstances" or "iam:ListUsers" ? "allowed" : "implicitDeny");
        iam.Setup(client => client.ListUsersAsync(It.IsAny<Amazon.IdentityManagement.Model.ListUsersRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.IdentityManagement.Model.ListUsersResponse { Users = [new() { UserName = "lecture-demo", Arn = "arn:aws:iam::000000000000:user/lecture-demo", Path = "/" }] });
        var ec2 = new Mock<Amazon.EC2.AmazonEC2Client>(new Amazon.Runtime.AnonymousAWSCredentials(), Amazon.RegionEndpoint.EUWest1) { CallBase = true };
        ec2.Setup(client => client.DescribeInstancesAsync(It.IsAny<Amazon.EC2.Model.DescribeInstancesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.EC2.Model.DescribeInstancesResponse { Reservations = [new() { Instances = [new() { InstanceId = "i-0123456789abcdef0", State = new() { Name = "running" }, Tags = [new("Name", "lecture-seule-demo")] }] }] });
        var ssm = new Mock<Amazon.SimpleSystemsManagement.IAmazonSimpleSystemsManagement>();
        ssm.Setup(client => client.DescribeInstanceInformationAsync(It.IsAny<Amazon.SimpleSystemsManagement.Model.DescribeInstanceInformationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.SimpleSystemsManagement.Model.DescribeInstanceInformationResponse { InstanceInformationList = [] });
        factory.Setup(client => client.CreateEc2Client()).Returns(ec2.Object);
        factory.Setup(client => client.CreateSsmClient()).Returns(ssm.Object);
        store.SetReadOnly(false);
        using var main = new MainViewModel(session, store, () => factory.Object);
        var shell = new MainWindow(main);
        awaitOnDispatcher(shell, async () => { await session.ConnectAsync("demo-sso", "eu-west-1", false); await main.PermissionsReady; });
        var model = (Ec2ViewModel)main.CurrentViewModel!;
        model.SelectedInstance = model.Instances.Single();
        var gate = PermissionGate.For(session.Context)!;
        awaitOnDispatcher(shell, () => gate.CheckAsync([new("ec2:StopInstances", "arn:aws:ec2:eu-west-1:000000000000:instance/i-0123456789abcdef0")]));
        Render(shell, "permissions-lecture-ec2", 1000, 640);
        Assert.IsTrue(Descendants<Button>(shell).Where(button => ReferenceEquals(button.Command, model.StopInstanceCommand)).All(button => !button.IsEnabled));
        var navigation = Descendants<ListBox>(shell).Single(list => ReferenceEquals(list.ItemsSource, main.Pages));
        foreach (var page in main.Pages)
        {
            navigation.ScrollIntoView(page);
            navigation.UpdateLayout();
            var item = (ListBoxItem)navigation.ItemContainerGenerator.ContainerFromItem(page);
            Assert.IsNotNull(item);
            Assert.AreEqual(page.Name is "EC2" or "IAM" or "Sessions" or "Mes accès", item.IsEnabled);
        }
        awaitOnDispatcher(shell, () => ((AsyncRelayCommand)model.StopInstanceCommand).ExecuteAsync(null));
        ec2.Verify(client => client.StopInstancesAsync(It.IsAny<Amazon.EC2.Model.StopInstancesRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        main.SelectedPage = main.Pages.Single(page => page.Name == "IAM");
        Render(shell, "permissions-iam-partiel", 1000, 640);
        var categories = Descendants<TabControl>(shell).Single(control => control.Name == "IamCategories");
        Assert.AreEqual("Users", ((IamViewModel)main.CurrentViewModel!).SelectedCategory.Id);
        Assert.AreEqual(1, categories.Items.Cast<object>().Count(item => ((TabItem)categories.ItemContainerGenerator.ContainerFromItem(item)).IsEnabled));
        iam.Setup(client => client.SimulatePrincipalPolicyAsync(It.IsAny<Amazon.IdentityManagement.Model.SimulatePrincipalPolicyRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Amazon.IdentityManagement.AmazonIdentityManagementServiceException("denied") { ErrorCode = "AccessDenied" });
        awaitOnDispatcher(shell, () => ((AsyncRelayCommand)main.RefreshPermissionsCommand).ExecuteAsync(null));
        Assert.IsTrue(main.Pages.Where(page => page.Name is not ("Sessions" or "Mes accès")).All(page => !page.IsEnabled));
        Render(shell, "permissions-non-verifiees", 1000, 640);
        shell.Close();
    }

    private static void awaitOnDispatcher(Window window, Func<Task> action, Func<string>? diagnostic = null)
    {
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var task = window.Dispatcher.Invoke(action);
        if (!task.IsCompleted)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            timer.Tick += (_, _) => frame.Continue = false;
            _ = task.ContinueWith(_ => window.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            timer.Stop();
            Assert.IsTrue(task.IsCompleted, "La verification simulee doit terminer. " + diagnostic?.Invoke());
        }
        task.GetAwaiter().GetResult();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private static void EditCell(DataGrid table, object item, int column, string value)
    {
        table.SelectedItem = item;
        table.CurrentCell = new DataGridCellInfo(item, table.Columns[column]);
        table.Focus();
        Assert.IsTrue(table.BeginEdit(), "La cellule doit rester editable avec le nouveau style.");
        table.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var editor = Descendants<TextBox>(table).Single();
        editor.Text = value;
        editor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Assert.IsTrue(table.CommitEdit(DataGridEditingUnit.Cell, true));
        Assert.IsTrue(table.CommitEdit(DataGridEditingUnit.Row, true));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Render(Window window, string name, int width, int height)
    {
        window.Width = width;
        window.Height = height;
        window.Show();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        Assert.IsTrue(window.ActualWidth > 0 && window.ActualHeight > 0);
        var pixelWidth = (int)Math.Ceiling(window.RenderSize.Width);
        var pixelHeight = (int)Math.Ceiling(window.RenderSize.Height);
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var pixels = new byte[pixelWidth * pixelHeight * 4];
        bitmap.CopyPixels(pixels, pixelWidth * 4, 0);
        Assert.IsTrue(pixels.Distinct().Count() > 8, "Le rendu ne doit pas etre vide.");
        var folder = Path.Combine(Path.GetTempPath(), "AwsManager-ui-check");
        Directory.CreateDirectory(folder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(output);
    }
}