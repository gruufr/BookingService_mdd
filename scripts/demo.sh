#!/usr/bin/env bash
# Минимальный сценарий «Ночлег» через curl.
# Запуск:  bash scripts/demo.sh                       (по умолчанию через шлюз http://localhost:8000/api)
#          BASE=http://localhost:8000/api bash scripts/demo.sh
set -u
BASE="${BASE:-http://localhost:8000/api}"
IN=$(date -d "+20 days" +%F 2>/dev/null || date -v+20d +%F)
OUT=$(date -d "+23 days" +%F 2>/dev/null || date -v+23d +%F)
EMAIL="student$RANDOM@example.com"

step() { printf '\n==== %s\n$ %s\n' "$1" "$2"; }
run()  { step "$1" "$2"; eval "$2"; echo; }

run "1. Все сервисы живы (health через шлюз)" \
  "for s in hotel booking user notification; do curl -s $BASE/health/\$s; echo; done"

run "2. hotel-service: города" "curl -s $BASE/hotels/cities"

run "3. hotel-service: отели в Казани" "curl -s '$BASE/hotels?city=%D0%9A%D0%B0%D0%B7%D0%B0%D0%BD%D1%8C'"

run "4. hotel-service: карточка отеля №4 (номера и отзывы)" "curl -s $BASE/hotels/4 | head -c 700"

run "5. booking-service: какие номера отеля №4 заняты с $IN по $OUT" \
  "curl -s '$BASE/availability?hotelId=4&checkIn=$IN&checkOut=$OUT'"

ROOM=$(curl -s $BASE/hotels/4 | grep -o '"rooms":\[{"id":[0-9]*' | grep -o '[0-9]*$')
BODY="{\"roomId\":$ROOM,\"checkIn\":\"$IN\",\"checkOut\":\"$OUT\",\"guests\":2,\"name\":\"Студент Демо\",\"email\":\"$EMAIL\"}"

run "6. booking-service: СОЗДАТЬ БРОНЬ (booking → hotel, user, notification)" \
  "curl -s -w '\nHTTP %{http_code}' -X POST $BASE/bookings -H 'Content-Type: application/json' -d '$BODY'"

run "7. Повторная бронь того же номера на те же даты → 409" \
  "curl -s -w '\nHTTP %{http_code}' -X POST $BASE/bookings -H 'Content-Type: application/json' -d '$BODY'"

run "8. user-service: гость создан автоматически" "curl -s '$BASE/users/by-email?email=$EMAIL'"

run "9. notification-service: уведомление записано" "curl -s '$BASE/notifications?email=$EMAIL'"

BID=$(curl -s "$BASE/bookings?email=$EMAIL" | grep -o '"id":[0-9]*' | head -1 | grep -o '[0-9]*')
run "10. booking-service: брони гостя" "curl -s '$BASE/bookings?email=$EMAIL'"

run "11. Отменить бронь №$BID" "curl -s -w '\nHTTP %{http_code}' -X POST $BASE/bookings/$BID/cancel"

run "12. Уведомления после отмены" "curl -s '$BASE/notifications?email=$EMAIL'"
