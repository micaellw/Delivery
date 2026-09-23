/// App-wide constants for the Rider App.
///
/// à·ÕÂº¡Ñº:
/// - .NET: `BackendApi/Security/AuthConstants.cs`
class AppConstants {
  AppConstants._();

  // ÄÄ App Info ÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄ
  static const String appName = String.fromEnvironment('APP_NAME', defaultValue: 'Smart Delivery');
  static const String appVersion = '1.0.0';

  // ÄÄ Storage Keys ÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄ
  /// Key ÊÓËÃÑºà¡çº JWT access token ã¹ SecureStorage
  static const String accessTokenKey = 'delivery_access_token';

  /// Key ÊÓËÃÑºà¡çº refresh token (¶éÒÁÕã¹Í¹Ò¤µ)
  static const String refreshTokenKey = 'delivery_refresh_token';

  /// Key ÊÓËÃÑºà¡çº user data
  static const String userDataKey = 'delivery_user_data';

  // ÄÄ Rider Status ÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄ
  /// µÃ§¡Ñº Rider.Status ã¹ BackendApi/Models/Entities/Rider.cs
  static const String statusAvailable = 'IDLE';
  static const String statusReserved = 'RESERVED';
  static const String statusBusy = 'BUSY';
  static const String statusStale = 'STALE';
  static const String statusOffline = 'OFFLINE';
  static const int riderHeartbeatIntervalSeconds = 10;

  // ÄÄ Order Status ÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄÄ
  /// µÃ§¡Ñº Order.Status ã¹ BackendApi/Models/Entities/Order.cs
  static const String orderAssigned = 'ASSIGNED';
  static const String orderDelivering = 'DELIVERING';
  static const String orderCompleted = 'COMPLETED';
  static const String orderCancelled = 'CANCELLED';

  // ÄÄ API Endpoints (relative to baseUrl /api/v1 — ËéÒÁ¢Öé¹µé¹´éÇÂ /) ÄÄ
  /// Dio ÃÇÁ path ¡Ñº baseUrl; path ·Õè¢Öé¹µé¹´éÇÂ / ¨ĞËÅØ´ÍÍ¡¨Ò¡ /api/v1
  static const String ridersEndpoint = 'riders';
  static const String ordersEndpoint = 'orders';
  static const String authEndpoint = 'auth';
}


