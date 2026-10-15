using ComfyUiRocmLauncher.Core.Models;
using ComfyUiRocmLauncher.Core.Services;

namespace ComfyUiRocmLauncher.Core.Test;

public class AppStateMachineTests
{
    [Fact]
    public void EnvBusy_Wins_HidesLaunchState_ThenRestores()
    {
        var machine = new AppStateMachine();
        var seen = new List<LaunchState>();
        machine.StateChanged += seen.Add;

        machine.ReportEnvBusy(true);
        machine.ReportLaunch(LaunchState.Running); // 环境任务进行中：只记录启动流程状态
        Assert.Equal(LaunchState.BusyEnv, machine.State);

        machine.ReportEnvBusy(false); // 释放后回到启动流程状态
        Assert.Equal(LaunchState.Running, machine.State);
        Assert.Equal(new[] { LaunchState.BusyEnv, LaunchState.Running }, seen);
    }

    [Fact]
    public void ReportLaunch_PublishesOnlyOnChange()
    {
        var machine = new AppStateMachine();
        var count = 0;
        machine.StateChanged += _ => count++;

        machine.ReportLaunch(LaunchState.Starting);
        machine.ReportLaunch(LaunchState.Running);
        machine.ReportLaunch(LaunchState.Running);

        Assert.Equal(2, count);
        Assert.Equal(LaunchState.Running, machine.State);
    }

    [Fact]
    public void ReportEnvBusy_IgnoresDuplicateSignals()
    {
        var machine = new AppStateMachine();
        var count = 0;
        machine.StateChanged += _ => count++;

        machine.ReportEnvBusy(true);
        machine.ReportEnvBusy(true);

        Assert.Equal(1, count);
    }
}

public class TaskRunnerTests
{
    [Fact]
    public void TryStart_EnforcesSingleSlotAndBusyFlagMatches()
    {
        var runner = new TaskRunner();

        Assert.True(runner.TryStart());
        Assert.True(runner.IsBusy);
        Assert.False(runner.TryStart()); // 第二个任务不得并行

        runner.Complete();
        Assert.False(runner.IsBusy);
        Assert.True(runner.TryStart()); // 槽位已释放
        runner.Complete();
    }

    [Fact]
    public void Complete_WithoutStart_DoesNotLeakSlot()
    {
        var runner = new TaskRunner();

        runner.Complete(); // 未占用时为空操作，不得重复 Release
        Assert.False(runner.IsBusy);
        Assert.True(runner.TryStart());
        runner.Complete();
    }

    [Fact]
    public void BusyChanged_FiresOnStartAndComplete()
    {
        var runner = new TaskRunner();
        var seen = new List<bool>();
        runner.BusyChanged += seen.Add;

        runner.TryStart();
        runner.Complete();

        Assert.Equal(new[] { true, false }, seen);
    }
}
