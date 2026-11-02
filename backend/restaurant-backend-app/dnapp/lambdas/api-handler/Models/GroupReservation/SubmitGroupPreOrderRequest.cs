using System.ComponentModel.DataAnnotations;

namespace APIHandler.Models.GroupReservation;

public record SubmitGroupPreOrderRequest([property: Required] string ReservationId);
