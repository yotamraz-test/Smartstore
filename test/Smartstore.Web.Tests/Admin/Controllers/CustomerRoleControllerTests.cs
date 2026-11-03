using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NUnit.Framework;
using Smartstore.Admin.Controllers;
using Smartstore.Core;
using Smartstore.Core.Identity;
using Smartstore.Core.Logging;
using Smartstore.Scheduling;

namespace Smartstore.Web.Tests.Admin.Controllers;

/// <summary>
/// Verifies the admin "Apply rules now" on-demand trigger, i.e. <see cref="CustomerRoleController.ApplyRules(int)"/>.
/// </summary>
[TestFixture]
public class CustomerRoleControllerTests
{
    private Mock<RoleManager<CustomerRole>> _roleManagerMock;
    private Mock<ITaskStore> _taskStoreMock;
    private Mock<ITaskScheduler> _taskSchedulerMock;
    private Mock<INotifier> _notifierMock;
    private CustomerRoleController _controller;

    [SetUp]
    public void SetUp()
    {
        _roleManagerMock = CreateRoleManagerMock();
        _taskStoreMock = new Mock<ITaskStore>();
        _taskSchedulerMock = new Mock<ITaskScheduler>();
        _notifierMock = new Mock<INotifier>();

        var commonServicesMock = new Mock<ICommonServices>();
        commonServicesMock.Setup(x => x.Notifier).Returns(_notifierMock.Object);

        // Only RoleManager, ITaskStore and ITaskScheduler are exercised by ApplyRules.
        // The remaining constructor dependencies are irrelevant to this action and are left null.
        _controller = new CustomerRoleController(
            db: null,
            roleManager: _roleManagerMock.Object,
            ruleService: null,
            currencyService: null,
            taskStore: new Lazy<ITaskStore>(() => _taskStoreMock.Object),
            taskScheduler: new Lazy<ITaskScheduler>(() => _taskSchedulerMock.Object),
            customerSettings: null)
        {
            Services = commonServicesMock.Object
        };
    }

    [TearDown]
    public void TearDown()
    {
        _controller?.Dispose();
    }

    /// <summary>
    /// Role and task both exist: the scheduler must be run for exactly that role, and the user informed.
    /// </summary>
    [Test]
    public async Task ApplyRules_Runs_Task_With_Single_Role_Id_When_Role_And_Task_Exist()
    {
        var role = new CustomerRole { Id = 42, Name = "Test role" };
        var task = new TaskDescriptor { Id = 7 };

        _roleManagerMock.Setup(x => x.FindByIdAsync(role.Id.ToString())).ReturnsAsync(role);
        _taskStoreMock.Setup(x => x.GetTaskByTypeAsync(It.IsAny<string>())).ReturnsAsync(task);

        IDictionary<string, string> capturedParameters = null;
        _taskSchedulerMock
            .Setup(x => x.RunSingleTaskAsync(It.IsAny<int>(), It.IsAny<IDictionary<string, string>>()))
            .Callback<int, IDictionary<string, string>>((_, parameters) => capturedParameters = parameters)
            .Returns(Task.CompletedTask);

        var result = await _controller.ApplyRules(role.Id);

        _taskStoreMock.Verify(x => x.GetTaskByTypeAsync(It.IsAny<string>()), Times.Once);
        _taskSchedulerMock.Verify(x => x.RunSingleTaskAsync(task.Id, It.IsAny<IDictionary<string, string>>()), Times.Once);

        Assert.That(capturedParameters, Is.Not.Null);
        Assert.That(capturedParameters, Has.Count.EqualTo(1));
        Assert.That(capturedParameters, Does.ContainKey("CustomerRoleIds"));
        Assert.That(capturedParameters["CustomerRoleIds"], Is.EqualTo(role.Id.ToString()));

        _notifierMock.Verify(x => x.Add(NotifyType.Info, It.IsAny<string>(), It.IsAny<bool>()), Times.Once);
        _notifierMock.Verify(x => x.Add(NotifyType.Error, It.IsAny<string>(), It.IsAny<bool>()), Times.Never);

        Assert.That(result, Is.TypeOf<RedirectToActionResult>());
        var redirect = (RedirectToActionResult)result;
        Assert.That(redirect.ActionName, Is.EqualTo(nameof(CustomerRoleController.Edit)));
        Assert.That(redirect.RouteValues["id"], Is.EqualTo(role.Id));
    }

    /// <summary>
    /// Role exists but the <c>TargetGroupEvaluatorTask</c> row is missing: the scheduler must never be
    /// invoked and the user must see an error notification instead of the progress notification.
    /// </summary>
    [Test]
    public async Task ApplyRules_Notifies_Error_And_Skips_Scheduler_When_Task_Not_Found()
    {
        var role = new CustomerRole { Id = 42, Name = "Test role" };

        _roleManagerMock.Setup(x => x.FindByIdAsync(role.Id.ToString())).ReturnsAsync(role);
        _taskStoreMock.Setup(x => x.GetTaskByTypeAsync(It.IsAny<string>())).ReturnsAsync((TaskDescriptor)null);

        var result = await _controller.ApplyRules(role.Id);

        _taskSchedulerMock.Verify(x => x.RunSingleTaskAsync(It.IsAny<int>(), It.IsAny<IDictionary<string, string>>()), Times.Never);

        _notifierMock.Verify(x => x.Add(NotifyType.Error, It.IsAny<string>(), It.IsAny<bool>()), Times.Once);
        _notifierMock.Verify(x => x.Add(NotifyType.Info, It.IsAny<string>(), It.IsAny<bool>()), Times.Never);

        Assert.That(result, Is.TypeOf<RedirectToActionResult>());
        var redirect = (RedirectToActionResult)result;
        Assert.That(redirect.ActionName, Is.EqualTo(nameof(CustomerRoleController.Edit)));
        Assert.That(redirect.RouteValues["id"], Is.EqualTo(role.Id));
    }

    /// <summary>
    /// Role does not exist: the action must return <see cref="NotFoundResult"/> without ever
    /// touching the task store or the scheduler.
    /// </summary>
    [Test]
    public async Task ApplyRules_Returns_NotFound_And_Skips_Task_Store_When_Role_Not_Found()
    {
        const int roleId = 99;

        _roleManagerMock.Setup(x => x.FindByIdAsync(roleId.ToString())).ReturnsAsync((CustomerRole)null);

        var result = await _controller.ApplyRules(roleId);

        Assert.That(result, Is.TypeOf<NotFoundResult>());

        _taskStoreMock.Verify(x => x.GetTaskByTypeAsync(It.IsAny<string>()), Times.Never);
        _taskSchedulerMock.Verify(x => x.RunSingleTaskAsync(It.IsAny<int>(), It.IsAny<IDictionary<string, string>>()), Times.Never);
    }

    /// <summary>
    /// <see cref="RoleManager{TRole}"/> has no public interface, so it is mocked directly via its
    /// virtual members (a store mock is only needed to satisfy the constructor).
    /// </summary>
    private static Mock<RoleManager<CustomerRole>> CreateRoleManagerMock()
    {
        var roleStore = new Mock<IRoleStore<CustomerRole>>();
        return new Mock<RoleManager<CustomerRole>>(roleStore.Object, null, null, null, null);
    }
}
