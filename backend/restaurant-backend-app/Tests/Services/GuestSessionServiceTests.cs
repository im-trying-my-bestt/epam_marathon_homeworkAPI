using Amazon.CognitoIdentityProvider.Model;
using APIHandler.Constants.Dishes;
using APIHandler.Constants.Orders;
using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Models.GuestSessions;
using APIHandler.Repositories.Auth;
using APIHandler.Repositories.Dishes;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.GuestSessions;
using APIHandler.Repositories.Orders;
using APIHandler.Repositories.Reservations;
using APIHandler.Repositories.Tables;
using APIHandler.Services.GuestSessions;
using AutoFixture.Xunit2;
using Moq;
using NPOI.POIFS.Storage;
using System.Security.Cryptography;
using System.Text;

namespace APIHandler.Tests.Services;

public class GuestSessionServiceTests
{
    private readonly Mock<IGuestSessionRepository> _mockGuestSessionRepository = new();
    private readonly Mock<IGroupReservationRepository> _mockGroupReservationRepository = new();
    private readonly Mock<IDishRepository> _mockDishRepository = new();
    private readonly Mock<IReservationRepository> _mockReservationRepository = new();
    private readonly Mock<IUserRepository> _mockUserRepository = new();
    private readonly Mock<ITableRepository> _mockTableRepository = new();
    private readonly Mock<IOrderRepository> _mockOrderRepository = new();
    private readonly GuestSessionService _guestSessionService;

    public GuestSessionServiceTests()
    {
        _guestSessionService = new GuestSessionService(
            _mockGroupReservationRepository.Object,
            _mockGuestSessionRepository.Object,
            _mockDishRepository.Object,
            _mockReservationRepository.Object,
            _mockUserRepository.Object,
            _mockTableRepository.Object,
            _mockOrderRepository.Object);
    }
    [Theory]
    [InlineAutoData(0)]
    [InlineAutoData(3)]
    public async Task CreateGuestSessionAsync_CorrectData_ReturnsSessionToken(
        int guestCount,
        CreateGuestSessionRequest request,
        GroupReservation groupReservation,
        Reservation parentReservation)
    {
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = parentReservation.Id;

        parentReservation.GuestCount = 5;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(parentReservation.Id))
            .ReturnsAsync(parentReservation);

        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync([]);

        GuestSession? session = null;
        _mockGuestSessionRepository
            .Setup(r => r.SaveAsync(It.IsAny<GuestSession>()))
            .Callback<GuestSession>(s => session = s);

        _mockGuestSessionRepository
            .Setup(r => r.CountByGroupReservationId(groupReservation.GroupReservationId))
            .ReturnsAsync(guestCount);

        var response = await _guestSessionService.CreateGuestSessionAsync(request);

        Assert.NotEmpty(response.SessionId);
        Assert.NotEmpty(response.SessionToken);
        Assert.NotNull(session);
        Assert.Equal(groupReservation.GroupReservationId, session.GroupReservationId);
        Assert.Equal(request.GuestName, session.GuestName);
        Assert.NotNull(session.SessionTokenHash);
        Assert.NotEmpty(session.SessionTokenHash);
    }

    [Theory]
    [AutoData]
    public async Task CreateGuestSessionAsync_InvalidGroupReservationId_ThrowsGroupReservationNotFoundException(CreateGuestSessionRequest request)
    {
        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(request.GroupReservationId))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(() => _guestSessionService.CreateGuestSessionAsync(request));
    }

    [Theory]
    [AutoData]
    public async Task CreateGuestSessionAsync_GroupReservationExpired_ThrowsGroupReservationExpiredException(CreateGuestSessionRequest request, GroupReservation groupReservation)
    {
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(request.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<GroupReservationExpiredException>(() => _guestSessionService.CreateGuestSessionAsync(request));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldAddNewDish_WhenDishDoesNotExistInSession(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, Dish dish)
    {
        guestSession.Dishes = [];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;
        dish.State = DishState.Available;
        int quantityToAdd = 2;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);
        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([dish]);

        await _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantityToAdd);

        Assert.Single(guestSession.Dishes);
        Assert.Equal(dish.Id, guestSession.Dishes[0].DishId);
        Assert.Equal(quantityToAdd, guestSession.Dishes[0].Quantity);
        Assert.Equal(OrderItemState.Pending, guestSession.Dishes[0].State);

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldIncreaseQuantity_WhenDishAlreadyExists(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, Dish dish)
    {
        int initialQuantity = 3;
        int quantityToAdd = 2;
        guestSession.Dishes = [new OrderItem { DishId = dish.Id, Quantity = initialQuantity, State = OrderItemState.Pending }];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;
        dish.State = DishState.Available;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);
        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([dish]);

        await _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantityToAdd);

        Assert.Single(guestSession.Dishes);
        Assert.Equal(initialQuantity + quantityToAdd, guestSession.Dishes[0].Quantity);

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [InlineAutoData(0)]
    [InlineAutoData(-1)]
    public async Task AddDishAsync_ShouldThrowValidationException_WhenQuantityIsZeroOrLess(int quantity, string guestSessionId, Dish dish)
    {
        dish.State = DishState.Available;

        _mockDishRepository
            .Setup(r => r.GetDishByIdAsync(dish.Id))
            .ReturnsAsync(dish);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.AddDishAsync(guestSessionId, dish.Id, quantity));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldThrowGuestSessionNotFoundException_WhenSessionNotFound(string guestSessionId, Dish dish)
    {
        dish.State = DishState.Available;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync((GuestSession?)null);

        _mockDishRepository
            .Setup(r => r.GetDishByIdAsync(dish.Id))
            .ReturnsAsync(dish);

        var exception = await Assert.ThrowsAsync<GuestSessionNotFoundException>(() =>
            _guestSessionService.AddDishAsync(guestSessionId, dish.Id, 2));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldThrowDishNotFound_WhenDishNotFound(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, string dishId)
    {
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);

        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([]);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.AddDishAsync(guestSession.Id, dishId, quantity: 2));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldThrowDishNotAvailable_WhenDishIsNotAvailable(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, Dish dish)
    {
        dish.State = DishState.OutOfStock;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);
        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([dish]);

        var exception = await Assert.ThrowsAsync<DishIsNotAvailableException>(() =>
            _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantity: 2));
    }

    [Theory]
    [InlineAutoData(GroupReservationStatus.Submitted, typeof(GroupReservationSubmittedException))]
    [InlineAutoData(GroupReservationStatus.Closed, typeof(GroupReservationExpiredException))]
    public async Task AddDishAsync_ShouldThrowGroupReservationExpired_WhenGroupStatusIsNotOpen(GroupReservationStatus status, Type exceptionType, GuestSession guestSession, GroupReservation groupReservation, Dish dish)
    {
        dish.State = DishState.Available;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = status;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockDishRepository
            .Setup(r => r.GetDishByIdAsync(dish.Id))
            .ReturnsAsync(dish);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        var exception = await Assert.ThrowsAsync(exceptionType, () =>
            _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantity: 2));
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldDecreaseQuantity_WhenQuantityIsGreaterThanAmountToRemove(GuestSession guestSession, GroupReservation groupReservation, string dishId)
    {
        int initialQuantity = 5;
        int quantityToRemove = 2;
        guestSession.Dishes =
        [
            new OrderItem { DishId = dishId, Quantity = initialQuantity, State = OrderItemState.Pending }
        ];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);


        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await _guestSessionService.RemoveDishAsync(guestSession.Id, dishId, quantityToRemove);

        Assert.Single(guestSession.Dishes);
        Assert.Equal(initialQuantity - quantityToRemove, guestSession.Dishes[0].Quantity);

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldRemoveDishFromList_WhenQuantityToRemoveIsEqualOrGreater(GuestSession guestSession, GroupReservation groupReservation, string dishId)
    {
        int initialQuantity = 2;
        int quantityToRemove = 3;
        guestSession.Dishes =
        [
            new OrderItem { DishId = dishId, Quantity = initialQuantity, State = OrderItemState.Pending }
        ];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await _guestSessionService.RemoveDishAsync(guestSession.Id, dishId, quantityToRemove);

        Assert.Empty(guestSession.Dishes);
        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldThrowDishNotFoundException_WhenDishDoesNotExistInSession(GuestSession guestSession, GroupReservation groupReservation, string dishId)
    {
        guestSession.Dishes = [];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
           .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
           .ReturnsAsync(groupReservation);

        var exception = await Assert.ThrowsAsync<DishNotFoundException>(() =>
            _guestSessionService.RemoveDishAsync(guestSession.Id, dishId, 2));

        Assert.Contains("not found in guest session", exception.Message);
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldThrowValidationException_WhenQuantityIsZeroOrLess(string guestSessionId, string dishId)
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.RemoveDishAsync(guestSessionId, dishId, 0));

        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.RemoveDishAsync(guestSessionId, dishId, -5));
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldThrowGuestSessionNotFoundException_WhenSessionNotFound(string guestSessionId, string dishId)
    {
        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync((GuestSession?)null);

        var exception = await Assert.ThrowsAsync<GuestSessionNotFoundException>(() =>
            _guestSessionService.RemoveDishAsync(guestSessionId, dishId, 2));
    }
    [Theory]
    [InlineAutoData(1, 0)]
    [InlineAutoData(1, 1)]
    [InlineAutoData(2, 1)]
    [InlineAutoData(2, 2)]
    public async Task CreateGuestSessionAsync_WhenCapacityReached_ThrowsGuestLimitExceededException(
        int reservationCapacity,
        int guestCount,
        CreateGuestSessionRequest request,
        GroupReservation groupReservation,
        Reservation parentReservation)
    {
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.ReservationId = parentReservation.Id;

        parentReservation.GuestCount = reservationCapacity;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(parentReservation.Id))
            .ReturnsAsync(parentReservation);

        _mockGuestSessionRepository
            .Setup(r => r.CountByGroupReservationId(groupReservation.GroupReservationId))
            .ReturnsAsync(guestCount);

        var exception = await Assert.ThrowsAsync<GuestLimitExceededException>(() => _guestSessionService.CreateGuestSessionAsync(request));

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Theory]
    [AutoData]
    public async Task CreateGuestSessionAsync_MissingParentReservation_ThrowsGroupReservationExpiredException(
        CreateGuestSessionRequest request,
        GroupReservation groupReservation)
    {
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(groupReservation.ReservationId))
            .ReturnsAsync((Reservation?)null);

        var exception = await Assert.ThrowsAsync<GroupReservationExpiredException>(() => _guestSessionService.CreateGuestSessionAsync(request));
    }

    [Theory]
    [AutoData]
    public async Task IsAuthorizedAsync_ValidTokenAndId_ReturnsTrue(GuestSession guestSession, byte[] tokenBytes)
    {
        string tokenString = Convert.ToBase64String(tokenBytes);
        var hashString = Convert.ToBase64String(SHA256.HashData(tokenBytes));
        guestSession.SessionTokenHash = hashString;

        _mockGuestSessionRepository
            .Setup(r => r.GetByTokenHash(hashString))
            .ReturnsAsync(guestSession);

        bool result = await _guestSessionService.IsAuthorizedAsync(tokenString, guestSession.Id);

        Assert.True(result);
    }

    [Theory]
    [AutoData]
    public async Task IsAuthorizedAsync_InvalidToken_ReturnsFalse(byte[] tokenBytes, string guestSessionId)
    {
        string tokenString = Convert.ToBase64String(tokenBytes);
        var hashString = Convert.ToBase64String(SHA256.HashData(tokenBytes));

        _mockGuestSessionRepository
            .Setup(r => r.GetByTokenHash(hashString))
            .ReturnsAsync((GuestSession?)null);

        bool result = await _guestSessionService.IsAuthorizedAsync(tokenString, guestSessionId);

        Assert.False(result);
    }

    [Theory]
    [AutoData]
    public async Task IsAuthorizedAsync_InvalidGuestSessionId_ReturnsFalse(GuestSession guestSession, byte[] tokenBytes, string guestSessionId)
    {
        string tokenString = Convert.ToBase64String(tokenBytes);
        var hashString = Convert.ToBase64String(SHA256.HashData(tokenBytes));
        guestSession.SessionTokenHash = hashString;

        _mockGuestSessionRepository
            .Setup(r => r.GetByTokenHash(hashString))
            .ReturnsAsync(guestSession);

        bool result = await _guestSessionService.IsAuthorizedAsync(tokenString, guestSessionId);

        Assert.False(result);
    }
    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_Valid_SubmitsSuccessfuilly(GuestSession guestSession, GroupReservation groupReservation, Dish[] dishes)
    {
        foreach (var dish in dishes)
            dish.State = DishState.Available;

        guestSession.IsSubmitted = false;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
           .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
           .ReturnsAsync(groupReservation);

        _mockDishRepository
            .Setup(d => d.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(dishes);

        GuestSession? session = null;
        _mockGuestSessionRepository
            .Setup(r => r.SaveAsync(It.IsAny<GuestSession>()))
            .Callback<GuestSession>(s => session = s);

        await _guestSessionService.SubmitCartAsync(guestSession.Id);

        Assert.NotNull(session);
        Assert.True(session.IsSubmitted);
    }

    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_SessionNotFound_ThrowsGuestSessionNotFound(string guestSessionId)
    {
        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync((GuestSession?)null);

        await Assert.ThrowsAsync<GuestSessionNotFoundException>(() => _guestSessionService.SubmitCartAsync(guestSessionId));
    }

    [Theory]
    [InlineAutoData(GroupReservationStatus.Submitted, typeof(GroupReservationSubmittedException))]
    [InlineAutoData(GroupReservationStatus.Closed, typeof(GroupReservationExpiredException))]
    public async Task SubmitCartAsync_GroupReservationNotOpen_ThrowsException(GroupReservationStatus status, Type exceptionType, GuestSession guestSession, GroupReservation groupReservation)
    {
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = status;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync(exceptionType, () => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }

    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_GuestSessionHasNoDishes_ThrowsCartIsEmptyException(GuestSession guestSession, GroupReservation groupReservation)
    {
        guestSession.Dishes = [];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<CartIsEmptyException>(() => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }

    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_GuestSessionSubmitted_ThrowsAlreadySubmited(GuestSession guestSession, GroupReservation groupReservation)
    {
        guestSession.IsSubmitted = true;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<GuestSessionSubmittedException>(() => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }

    [Theory]
    [InlineAutoData(DishState.OutOfStock)]
    [InlineAutoData(DishState.OnStop)]
    public async Task SubmitCartAsync_DishNotAvailable_ThrowsDishNotAvailable(DishState state, Dish dish, GuestSession guestSession, GroupReservation groupReservation)
    {
        dish.State = state;
        guestSession.IsSubmitted = false;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        guestSession.Dishes = 
        [
            new OrderItem()
            {
                DishId = dish.Id,
                Quantity = 1,
            }
        ];

        _mockDishRepository
            .Setup(d => d.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([dish]);

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
           .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
           .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<DishIsNotAvailableException>(() => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }
    [Theory]
    [MemberAutoData(nameof(PepareDishesForGuestSession))]
    public async Task GetGuestSessionInfoAsync_Guest_ReturnsAsync(
        IEnumerable<string> dishIds,
        IEnumerable<Dish> dishes,
        List<OrderItem> orderItems,
        decimal expectedTotal,
        GuestSession session)
    {
        session.Dishes = orderItems;
        session.IsOwnerSession = false;

        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(dishIds))
            .ReturnsAsync(dishes);

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(session.Id))
            .ReturnsAsync(session);

        GetGuestSessionResponse result = await _guestSessionService.GetGuestSessionInfoAsync(session.Id);

        Assert.Equal(session.GuestName, result.Name);
        Assert.Equal(dishes.Count(), result.Items.Count());
        Assert.Equal(expectedTotal, result.Total);
    }

    [Theory]
    [MemberAutoData(nameof(PepareDishesForGuestSession))]
    public async Task GetGuestSessionInfoAsync_GroupOwner_ReturnsAsync(
        IEnumerable<string> dishIds,
        IEnumerable<Dish> dishes,
        List<OrderItem> orderItems,
        decimal expectedTotal,
        GuestSession session,
        GroupReservation groupReservation,
        Reservation reservation,
        string customerId,
        string ownerName,
        UserType userType)
    {
        session.Dishes = orderItems;
        session.IsOwnerSession = true;
        session.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.ReservationId = reservation.Id;
        reservation.CustomerId = customerId;
        userType.Attributes.Add(new AttributeType()
        {
            Name = "given_name",
            Value = ownerName,
        });

        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(dishIds))
            .ReturnsAsync(dishes);

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(session.Id))
            .ReturnsAsync(session);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _mockUserRepository
            .Setup(r => r.GetUserByIdAsync(customerId))
            .ReturnsAsync(userType);

        GetGuestSessionResponse result = await _guestSessionService.GetGuestSessionInfoAsync(session.Id);

        Assert.Equal(ownerName, result.Name);
        Assert.Equal(dishes.Count(), result.Items.Count());
        Assert.Equal(expectedTotal, result.Total);
    }

    private const string OwnerId = "owner-1";
    private const string GroupId = "group-1";
    private const string ReservationId = "reservation-1";

    private (GroupReservation Group, Reservation Reservation) SetupOwnedGroup(
        GroupReservationStatus status = GroupReservationStatus.Open)
    {
        var reservation = new Reservation
        {
            Id = ReservationId,
            TableId = "table-1",
            WaiterId = "waiter-1",
            CustomerId = OwnerId
        };

        var group = new GroupReservation
        {
            GroupReservationId = GroupId,
            ReservationId = ReservationId,
            ReservationToken = "token",
            Status = status
        };

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(GroupId))
            .ReturnsAsync(group);
        _mockGroupReservationRepository
            .Setup(r => r.GetByReservationIdAsync(ReservationId))
            .ReturnsAsync(group);
        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync(reservation);

        return (group, reservation);
    }

    private static GuestSession CreateSession(string id, string name, bool isSubmitted, params (string DishId, int Quantity)[] dishes)
        => new()
        {
            Id = id,
            GuestName = name,
            GroupReservationId = GroupId,
            IsSubmitted = isSubmitted,
            Dishes = dishes.Select(d => new OrderItem { DishId = d.DishId, Quantity = d.Quantity }).ToList()
        };

    private static Dish CreateDish(string id, string name, decimal price) => new() { Id = id, Name = name, Price = price };

    private void SetupDishes(params Dish[] dishes)
        => _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(dishes);

    private static Order CreateOrder(params OrderItem[] items) => new()
    {
        OrderId = "order-1",
        CustomerId = OwnerId,
        ReservationId = ReservationId,
        ReservationAddress = "address",
        ReservationTime = "time",
        Dishes = items.ToList(),
        Status = OrderStatus.Created
    };

    [Fact]
    public async Task GetGroupSessionsAsync_Owner_ReturnsParticipantsAndTotals()
    {
        SetupOwnedGroup();
        SetupDishes(CreateDish("dish1", "Caesar Salad", 180), CreateDish("dish2", "Tom Yum", 220));
        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync([
                CreateSession("session1", "Andrii", true, ("dish1", 2)),
                CreateSession("session2", "Olena", false, ("dish2", 1))
            ]);

        var result = await _guestSessionService.GetGroupSessionsAsync(OwnerId, GroupId);

        var participants = result.Participants.ToList();
        Assert.Equal(2, participants.Count);
        Assert.Equal("Andrii", participants[0].Name);
        Assert.Equal(360, participants[0].Total);
        Assert.True(participants[0].IsSubmitted);
        Assert.Equal(ParticipantType.Guest, participants[0].Type);
        Assert.Equal("Olena", participants[1].Name);
        Assert.Equal(220, participants[1].Total);
        Assert.False(participants[1].IsSubmitted);
        Assert.Equal(580, result.Total);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_OwnerSession_IsReturnedWithOwnerType()
    {
        var (_, reservation) = SetupOwnedGroup();
        var userType = new UserType
        {
            Attributes = new List<AttributeType>
    {
        new AttributeType { Name = "given_name", Value = "Ivan" }
    }
        }; _mockUserRepository.Setup(r => r.GetUserByIdAsync(reservation.CustomerId!)).ReturnsAsync(userType);
        SetupDishes(CreateDish("dish1", "Caesar Salad", 180));

        var ownerSession = CreateSession("owner-session", "Owner", true, ("dish1", 1));
        ownerSession.IsOwnerSession = true;
        ownerSession.OwnerUserId = OwnerId;
        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync([ownerSession]);

        var result = await _guestSessionService.GetGroupSessionsAsync(OwnerId, GroupId);

        var participant = Assert.Single(result.Participants);
        Assert.Equal(ParticipantType.Owner, participant.Type);
        Assert.Equal("Ivan", participant.Name);
        Assert.Equal(180, result.Total);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_NoSessions_ReturnsEmptyListAndZeroTotal()
    {
        SetupOwnedGroup();
        SetupDishes();
        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync([]);

        var result = await _guestSessionService.GetGroupSessionsAsync(OwnerId, GroupId);

        Assert.Empty(result.Participants);
        Assert.Equal(0, result.Total);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_NotOwner_ThrowsActionForbidden()
    {
        SetupOwnedGroup();

        await Assert.ThrowsAsync<ActionForbiddenException>(
            () => _guestSessionService.GetGroupSessionsAsync("someone-else", GroupId));
    }

    [Fact]
    public async Task GetGroupSessionsAsync_GroupNotFound_ThrowsGroupReservationNotFound()
    {
        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(GroupId))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(
            () => _guestSessionService.GetGroupSessionsAsync(OwnerId, GroupId));
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_Valid_DeletesAndReturnsUpdatedSnapshot()
    {
        SetupOwnedGroup();
        SetupDishes(CreateDish("dish2", "Tom Yum", 220));
        var toDelete = CreateSession("session1", "Andrii", true, ("dish1", 2));
        var remaining = CreateSession("session2", "Olena", false, ("dish2", 1));

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId("session1")).ReturnsAsync(toDelete);
        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync([remaining]);

        var result = await _guestSessionService.DeleteGuestSessionAsync(OwnerId, GroupId, "session1");

        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(toDelete), Times.Once);
        var participant = Assert.Single(result.Participants);
        Assert.Equal("session2", participant.Id);
        Assert.Equal(220, result.Total);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_NotOwner_ThrowsActionForbidden()
    {
        SetupOwnedGroup();

        await Assert.ThrowsAsync<ActionForbiddenException>(
            () => _guestSessionService.DeleteGuestSessionAsync("someone-else", GroupId, "session1"));
        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_SessionNotFound_ThrowsGuestSessionNotFound()
    {
        SetupOwnedGroup();
        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId("missing")).ReturnsAsync((GuestSession?)null);

        await Assert.ThrowsAsync<GuestSessionNotFoundException>(
            () => _guestSessionService.DeleteGuestSessionAsync(OwnerId, GroupId, "missing"));
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_SessionFromAnotherGroup_ThrowsValidationException()
    {
        SetupOwnedGroup();
        var foreign = CreateSession("session1", "Andrii", false);
        foreign.GroupReservationId = "another-group";
        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId("session1")).ReturnsAsync(foreign);

        await Assert.ThrowsAsync<ValidationException>(
            () => _guestSessionService.DeleteGuestSessionAsync(OwnerId, GroupId, "session1"));
        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_OwnerSession_ThrowsValidationException()
    {
        SetupOwnedGroup();
        var ownerSession = CreateSession("owner-session", "Owner", true);
        ownerSession.IsOwnerSession = true;
        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId("owner-session")).ReturnsAsync(ownerSession);

        await Assert.ThrowsAsync<ValidationException>(
            () => _guestSessionService.DeleteGuestSessionAsync(OwnerId, GroupId, "owner-session"));
        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_OwnerSessionExists_ReturnsItsId()
    {
        SetupOwnedGroup();
        var ownerSession = CreateSession("owner-session-123", "Owner", false);
        ownerSession.IsOwnerSession = true;
        ownerSession.OwnerUserId = OwnerId;
        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync([CreateSession("guest", "Andrii", false), ownerSession]);

        var result = await _guestSessionService.GetOwnerGuestSessionIdAsync(OwnerId, ReservationId);

        Assert.Equal("owner-session-123", result.GuestSessionId);
        _mockGuestSessionRepository.Verify(r => r.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_NoOwnerSession_CreatesOne()
    {
        SetupOwnedGroup();
        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync([CreateSession("guest", "Andrii", false)]);

        GuestSession? saved = null;
        _mockGuestSessionRepository
            .Setup(r => r.SaveAsync(It.IsAny<GuestSession>()))
            .Callback<GuestSession>(s => saved = s);

        var result = await _guestSessionService.GetOwnerGuestSessionIdAsync(OwnerId, ReservationId);

        Assert.NotNull(saved);
        Assert.True(saved!.IsOwnerSession);
        Assert.Equal(OwnerId, saved.OwnerUserId);
        Assert.Equal(GroupId, saved.GroupReservationId);
        Assert.Equal(saved.Id, result.GuestSessionId);
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_ReservationNotFound_ThrowsReservationNotFound()
    {
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(ReservationId)).ReturnsAsync((Reservation?)null);

        await Assert.ThrowsAsync<ReservationNotFoundException>(
            () => _guestSessionService.GetOwnerGuestSessionIdAsync(OwnerId, ReservationId));
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_GroupNotFound_ThrowsGroupReservationNotFound()
    {
        SetupOwnedGroup();
        _mockGroupReservationRepository
            .Setup(r => r.GetByReservationIdAsync(ReservationId))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(
            () => _guestSessionService.GetOwnerGuestSessionIdAsync(OwnerId, ReservationId));
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_NotOwner_ThrowsActionForbidden()
    {
        SetupOwnedGroup();

        await Assert.ThrowsAsync<ActionForbiddenException>(
            () => _guestSessionService.GetOwnerGuestSessionIdAsync("someone-else", ReservationId));
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_Valid_MergesSubmittedSessionsAndUpdatesStatuses()
    {
        var (group, _) = SetupOwnedGroup();
        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync([
                CreateSession("s1", "Andrii", true, ("dish1", 2), ("dish2", 1)),
                CreateSession("s2", "Olena", true, ("dish1", 1), ("dish2", 1)),
                CreateSession("s3", "Not submitted", false, ("dish1", 10), ("dish3", 5))
            ]);

        var order = CreateOrder();
        _mockOrderRepository.Setup(r => r.GetOrderByReservationIdAsync(ReservationId)).ReturnsAsync(order);

        var result = await _guestSessionService.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId);

        Assert.Equal(2, result.Count);
        Assert.Equal(3, result.Single(d => d.DishId == "dish1").Quantity);
        Assert.Equal(2, result.Single(d => d.DishId == "dish2").Quantity);
        Assert.DoesNotContain(result, d => d.DishId == "dish3");

        Assert.Equal(OrderStatus.SubmittedByCustomer, order.Status);
        Assert.Equal(2, order.Dishes.Count);
        _mockOrderRepository.Verify(r => r.SaveOrderAsync(order), Times.Once);

        Assert.Equal(GroupReservationStatus.Submitted, group.Status);
        _mockGroupReservationRepository.Verify(r => r.SaveGroupReservationAsync(group), Times.Once);
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_OwnerSession_IsNotIncluded()
    {
        SetupOwnedGroup();

        var guestSession = CreateSession(
            "guest1", "Andrii", true, ("dish1", 2));

        var ownerSession = CreateSession(
            "owner1", "Owner", true, ("dish2", 5));

        ownerSession.IsOwnerSession = true;
        ownerSession.OwnerUserId = OwnerId;

        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync([guestSession, ownerSession]);

        var order = CreateOrder();

        _mockOrderRepository
            .Setup(r => r.GetOrderByReservationIdAsync(ReservationId))
            .ReturnsAsync(order);

        var result = await _guestSessionService.SubmitGroupPreOrderAsync(
            OwnerId, GroupId, ReservationId);

        Assert.Single(result);
        Assert.Equal("dish1", result[0].DishId);
        Assert.Equal(2, result[0].Quantity);
    }


    [Fact]
    public async Task SubmitGroupPreOrderAsync_DishAlreadyInOrder_IncreasesQuantity()
    {
        SetupOwnedGroup();
        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync([CreateSession("s1", "Andrii", true, ("dish1", 2))]);

        var order = CreateOrder(new OrderItem { DishId = "dish1", Quantity = 1 });
        _mockOrderRepository.Setup(r => r.GetOrderByReservationIdAsync(ReservationId)).ReturnsAsync(order);

        await _guestSessionService.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId);

        var item = Assert.Single(order.Dishes);
        Assert.Equal(3, item.Quantity);
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_GroupNotFound_ThrowsGroupReservationNotFound()
    {
        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(GroupId))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(
            () => _guestSessionService.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_ReservationNotFound_ThrowsReservationNotFound()
    {
        SetupOwnedGroup();
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(ReservationId)).ReturnsAsync((Reservation?)null);

        await Assert.ThrowsAsync<ReservationNotFoundException>(
            () => _guestSessionService.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_NotOwner_ThrowsActionForbidden()
    {
        SetupOwnedGroup();

        await Assert.ThrowsAsync<ActionForbiddenException>(
            () => _guestSessionService.SubmitGroupPreOrderAsync("someone-else", GroupId, ReservationId));
        _mockOrderRepository.Verify(r => r.SaveOrderAsync(It.IsAny<Order>()), Times.Never);
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_ReservationDoesNotMatchGroup_ThrowsValidationException()
    {
        SetupOwnedGroup();
        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync("other-reservation"))
            .ReturnsAsync(new Reservation { Id = "other-reservation", TableId = "t", WaiterId = "w", CustomerId = OwnerId });

        await Assert.ThrowsAsync<ValidationException>(
            () => _guestSessionService.SubmitGroupPreOrderAsync(OwnerId, GroupId, "other-reservation"));
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_AlreadySubmitted_ThrowsGroupReservationSubmitted()
    {
        SetupOwnedGroup(GroupReservationStatus.Submitted);

        await Assert.ThrowsAsync<GroupReservationSubmittedException>(
            () => _guestSessionService.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_NoSubmittedSessions_ThrowsValidationException()
    {
        SetupOwnedGroup();
        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync([CreateSession("s1", "Andrii", false, ("dish1", 1))]);

        await Assert.ThrowsAsync<ValidationException>(
            () => _guestSessionService.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_SubmittedSessionsHaveNoDishes_ThrowsValidationException()
    {
        SetupOwnedGroup();
        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync([CreateSession("s1", "Andrii", true)]);

        await Assert.ThrowsAsync<ValidationException>(
            () => _guestSessionService.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));
    }

    [Fact]
    public async Task SubmitGroupPreOrderAsync_OrderNotFound_ThrowsValidationException()
    {
        var (group, _) = SetupOwnedGroup();
        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(GroupId))
            .ReturnsAsync([CreateSession("s1", "Andrii", true, ("dish1", 1))]);
        _mockOrderRepository.Setup(r => r.GetOrderByReservationIdAsync(ReservationId)).ReturnsAsync((Order?)null);

        await Assert.ThrowsAsync<ValidationException>(
            () => _guestSessionService.SubmitGroupPreOrderAsync(OwnerId, GroupId, ReservationId));
        Assert.Equal(GroupReservationStatus.Open, group.Status);
        _mockGroupReservationRepository.Verify(r => r.SaveGroupReservationAsync(It.IsAny<GroupReservation>()), Times.Never);
    }

    public static TheoryData<IEnumerable<string>, IEnumerable<Dish>, List<OrderItem>, decimal> PepareDishesForGuestSession()
    {
        var dishMap = new Dictionary<string, Dish>()
        {
            ["1"] = new()
            {
                Id = "1",
                Price = 1
            },
            ["2"] = new()
            {
                Id = "2",
                Price = 2
            },
            ["3"] = new()
            {
                Id = "3",
                Price = 3
            }
        };

        var orderItems = new List<OrderItem>
        {
            new OrderItem()
                    {
                        DishId = "1",
                        Quantity = 2
                    },
                    new OrderItem()
                    {
                        DishId = "2",
                        Quantity = 3
                    },
                    new OrderItem()
                    {
                        DishId = "3",
                        Quantity = 1
                    }
        };

        var expectedTotal = orderItems.Sum(o => dishMap[o.DishId].Price * o.Quantity);

        return new()
        {
            { dishMap.Keys, dishMap.Values, orderItems, expectedTotal }
        };
    }
}
