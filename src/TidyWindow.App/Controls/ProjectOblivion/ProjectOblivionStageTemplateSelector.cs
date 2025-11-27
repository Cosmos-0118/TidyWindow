using System.Windows;
using System.Windows.Controls;
using TidyWindow.App.ViewModels;

namespace TidyWindow.App.Controls.ProjectOblivion;

public sealed class ProjectOblivionStageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? KickoffTemplate { get; set; }
    public DataTemplate? DefaultUninstallTemplate { get; set; }
    public DataTemplate? ProcessSweepTemplate { get; set; }
    public DataTemplate? ArtifactDiscoveryTemplate { get; set; }
    public DataTemplate? SelectionHoldTemplate { get; set; }
    public DataTemplate? CleanupTemplate { get; set; }
    public DataTemplate? SummaryTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not ProjectOblivionTimelineStageViewModel stageVm)
        {
            return base.SelectTemplate(item, container);
        }

        return stageVm.Stage switch
        {
            ProjectOblivionStage.Kickoff => KickoffTemplate ?? base.SelectTemplate(item, container),
            ProjectOblivionStage.DefaultUninstall => DefaultUninstallTemplate ?? base.SelectTemplate(item, container),
            ProjectOblivionStage.ProcessSweep => ProcessSweepTemplate ?? base.SelectTemplate(item, container),
            ProjectOblivionStage.ArtifactDiscovery => ArtifactDiscoveryTemplate ?? base.SelectTemplate(item, container),
            ProjectOblivionStage.SelectionHold => SelectionHoldTemplate ?? base.SelectTemplate(item, container),
            ProjectOblivionStage.Cleanup => CleanupTemplate ?? base.SelectTemplate(item, container),
            ProjectOblivionStage.Summary => SummaryTemplate ?? base.SelectTemplate(item, container),
            _ => base.SelectTemplate(item, container)
        };
    }
}
