namespace TokenConsumptionMonitoring.Models;

public enum ConnectionStatus { Unknown, Ok, Warn, Critical, AuthError, Offline }

public enum AlertLevel { None, Warn, Critical }
