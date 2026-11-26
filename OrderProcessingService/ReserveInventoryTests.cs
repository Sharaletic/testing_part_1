using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Shop.Orders.Tests;

[TestFixture]
public class ReserveInventoryTests
{
    private Mock<IInventoryService> _inventory = null!;
    private Mock<IPaymentGateway> _payments = null!;
    private Mock<IOrderRepository> _orders = null!;

    [SetUp]
    public void SetUp()
    {
        _inventory = new Mock<IInventoryService>();
        _inventory.Setup(i => i.TryReserveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _inventory.Setup(i => i.ReleaseAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _payments = new Mock<IPaymentGateway>();
        _payments.Setup(p => p.ChargeAsync(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentResult(true, false, "tx-1", null));

        _orders = new Mock<IOrderRepository>();
        _orders.Setup(r => r.GetByIdempotencyKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Order?)null);
        _orders.Setup(r => r.SaveAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    // если все товары есть на складе, каждый товар должен быть зарезервирован
    [Test]
    public async Task PlaceOrderAsync_AllItemsInStock_ReservesEveryLine()
    {
        var result = await CreateSut().PlaceOrderAsync(Request(
            new OrderLine("A", 1, 10m),
            new OrderLine("B", 2, 10m),
            new OrderLine("C", 3, 10m)));

        Assert.That(result.IsSuccess, Is.True);
        // проверка сколько раз был вызван метод TryReserveAsync для каждого товара
        _inventory.Verify(i => i.TryReserveAsync("A", 1, It.IsAny<CancellationToken>()), Times.Once());
        _inventory.Verify(i => i.TryReserveAsync("B", 2, It.IsAny<CancellationToken>()), Times.Once());
        _inventory.Verify(i => i.TryReserveAsync("C", 3, It.IsAny<CancellationToken>()), Times.Once());
        _inventory.Verify(i => i.ReleaseAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    // если первого товара нет, заказ должен завершиться ошибкой.
    [Test]
    public async Task PlaceOrderAsync_FirstItemOutOfStock_ReturnsFailureWithoutReleasing()
    {
        _inventory.Setup(i => i.TryReserveAsync("A", 1, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateSut().PlaceOrderAsync(Request(new OrderLine("A", 1, 10m)));

        Assert.That(result.Failure, Is.EqualTo(FailureReason.OutOfStock));
        _inventory.Verify(i => i.ReleaseAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never());
        _payments.Verify(p => p.ChargeAsync(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    // если первые товары успешно зарезервированы, а следующий товар отсутствует, нужно освободить только ранее зарезервированные товары
    [Test]
    public async Task PlaceOrderAsync_LaterItemOutOfStock_ReleasesOnlyReservedItems()
    {
        _inventory.Setup(i => i.TryReserveAsync("C", 3, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateSut().PlaceOrderAsync(Request(
            new OrderLine("A", 1, 10m),
            new OrderLine("B", 2, 10m),
            new OrderLine("C", 3, 10m),
            new OrderLine("D", 4, 10m)));

        Assert.That(result.Failure, Is.EqualTo(FailureReason.OutOfStock));
        _inventory.Verify(i => i.ReleaseAsync("A", 1, CancellationToken.None), Times.Once());
        _inventory.Verify(i => i.ReleaseAsync("B", 2, CancellationToken.None), Times.Once());
        _inventory.Verify(i => i.ReleaseAsync("C", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never());
        _inventory.Verify(i => i.TryReserveAsync("D", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never());
        _payments.Verify(p => p.ChargeAsync(It.IsAny<PaymentRequest>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    private OrderProcessingService CreateSut() => new(
        _orders.Object,
        _inventory.Object,
        _payments.Object,
        Mock.Of<IDiscountEngine>(d => d.GetApplicableAsync(
            It.IsAny<Customer>(), It.IsAny<IReadOnlyList<OrderLine>>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())
            == Task.FromResult<IReadOnlyList<Discount>>(Array.Empty<Discount>())),
        Mock.Of<ITaxCalculator>(),
        Mock.Of<INotificationService>(n => n.SendConfirmationAsync(
            It.IsAny<Order>(), It.IsAny<string>(), It.IsAny<CancellationToken>()) == Task.CompletedTask),
        Mock.Of<IUnitOfWork>(u => u.ExecuteAsync(
            It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>()) == Task.FromResult(true)),
        Mock.Of<IClock>(c => c.UtcNow == DateTimeOffset.UtcNow),
        Mock.Of<IDelayer>(),
        new OrderOptions(),
        Mock.Of<ILogger<OrderProcessingService>>());

    private static PlaceOrderRequest Request(params OrderLine[] lines) => new(
        "key-1",
        new Customer(Guid.NewGuid(), "test@example.com", CustomerTier.Standard, "RU"),
        lines,
        "token");
}
