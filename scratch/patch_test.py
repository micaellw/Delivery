path = 'B:/Delivery/RootScripts/scripts.test/test/BackendApi.IntegrationTests/Hubs/TrackingHubRegressionIntegrationTests.cs'
with open(path, 'r', encoding='utf-8') as f:
    content = f.read()

old_str = '''        var userId = root.GetProperty("user").GetProperty("id").GetString()!;
        return (token, userId);'''

new_str = '''        var riderId = root.GetProperty("user").GetProperty("riderId").GetString()!;
        return (token, riderId);'''

if old_str in content:
    content = content.replace(old_str, new_str)
    with open(path, 'w', encoding='utf-8') as f:
        f.write(content)
    print("SUCCESS")
else:
    print("NOT FOUND")
