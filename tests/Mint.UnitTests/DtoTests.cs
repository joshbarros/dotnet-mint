using Mint.Orders;
using Mint.Drops;
using Xunit;

namespace Mint.UnitTests;

public class DtoTests
{
    [Fact]
    public void OrderDto_MapsEveryFieldAndTheStatusName()
    {
        var now = DateTimeOffset.UtcNow;
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            DropId = Guid.NewGuid(),
            DropCode = "S-1",
            Reference = "COL-1",
            Units = 3,
            TotalCents = 277_500,
            Status = OrderStatus.Sold,
            CertificateNumber = "MC-1",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var dto = OrderDto.From(order);

        Assert.Equal(order.Id, dto.Id);
        Assert.Equal(order.OwnerId, dto.OwnerId);
        Assert.Equal("Sold", dto.Status);
        Assert.Equal(277_500, dto.TotalCents);
        Assert.Equal("MC-1", dto.CertificateNumber);
    }

    [Fact]
    public void DropDto_MapsTheScarceNumber()
    {
        var drop = new Drop { Id = Guid.NewGuid(), Code = "S-1", Name = "v", Series = "o", Artist = "d", UnitsTotal = 10, UnitsLeft = 4, MaxPerCollector = 3 };
        var dto = DropDto.From(drop);
        Assert.Equal(10, dto.UnitsTotal);
        Assert.Equal(3, dto.MaxPerCollector);
        Assert.Equal(4, dto.UnitsLeft);
    }
}
