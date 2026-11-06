-- Выполняется один раз, при первом создании volume db_data.
-- У каждого сервиса своя база (принцип database-per-service).
CREATE DATABASE hotels_db;
CREATE DATABASE bookings_db;
CREATE DATABASE users_db;
CREATE DATABASE notifications_db;
