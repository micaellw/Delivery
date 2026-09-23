path = r"B:\Delivery\RootScripts\scripts.test\test\BackendApi.IntegrationTests\Telemetry\DualWriteFailureRecoveryIntegrationTests.cs"
with open(path, "r", encoding="utf-8") as f:
    text = f.read()

text = text.replace('v.Name == "EventId"', 'v.Name == "eventId"')

with open(path, "w", encoding="utf-8") as f:
    f.write(text)

print("SUCCESS: Fixed casing to eventId")
