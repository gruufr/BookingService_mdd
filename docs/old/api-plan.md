# План API и сервисов бронирования (C# Interface)

## Интерфейс сервиса (IBookingService):
- `CreateBooking(BookingRequest request)` — создание брони
- `GetBookingById(Guid id)` — получение сведений о бронировании
- `CancelBooking(Guid id)` — отмена брони
- `GetAvailableRooms()` — список доступных мест

- POST /api/v1/bookings — создание бронирования
