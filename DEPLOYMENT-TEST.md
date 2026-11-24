# Connected synthetic-data test deployment

Not production. Never enter real patient details. Free hosting may sleep or suspend at quota. No card, upgrade, paid add-on, or chargeable overage is authorized.

## Backend environment

Use the Dockerfile on a reviewed commit, Postgres 16, and a new isolated database. Do not reuse a real clinic database.

- `ASPNETCORE_ENVIRONMENT=Staging`
- `Demo__SeedSyntheticData=true`
- `ConnectionStrings__Default`: private Npgsql connection string with TLS
- `Jwt__Key`: separate generated secret, at least 32 characters
- `Auth__FixedOtp`: private generated six-digit test PIN, never the public development default
- `Payments__PublicBaseUrl`: actual HTTPS backend URL
- `Storage__Path=/tmp/moonlight-demo/uploads`
- `Storage__RecordsPath=/tmp/moonlight-demo/private-records`
- `Cors__AllowedOrigins__0`, etc: exact deployed Website, Dashboard and Flutter web origins, never `*`

The explicit hosted-demo switch only works in Staging. It migrates and seeds synthetic clinic data, rejects the public default OTP, rejects live payment/WhatsApp/FCM credentials and a Console API key, forces scheduled jobs/reminders off, disables sensitive feature flags and rejects form uploads. Swagger stays off. Photos/files are not durable on Render Free. Do not turn uploads on.

Use the seeded role phone identifiers in README with the private test PIN, shared only through the secure credential flow. Do not publish the PIN, JWT, database connection string, or Console admin login in code, PRs, logs, screenshots or build output.

Console integration is deliberately not enabled initially: its provisioning mode must remain DryRun with no GitHub token. Clinic branding/features use repository defaults. This is a limitation, not a completed cross-console integration test.

## Free-tier limits

Render Free dynamic services share 750 running hours/workspace/month, sleep after 15 idle minutes, and can be suspended at quota. Three dynamic services cannot all remain running all month. Static sites do not consume those dynamic hours. Never add a card or select paid compute. Render Postgres Free expires after 30 days and is not selected.

Neon Free is the database candidate: select Postgres 16, separate backend/console databases or projects, and confirm Free at resource creation. Current documented limits are 100 CU-hours/project/month, 1 GB database/project and 5 GB egress/project. Quota suspends compute; no paid upgrade is allowed.

Sources checked November 23, 2026:
- https://render.com/docs/free
- https://neon.com/docs/introduction/plans

## Remaining acceptance tests

After reviewed changes merge and resources deploy: verify HTTPS health, exact CORS allow-list, private OTP login, synthetic booking/cancel across Website and Dashboard, Flutter web login/booking, Console authentication and API route behavior, denied anonymous/staff/patient cross-role calls, disabled uploads/payment/messaging, no default secrets, and final $0 account billing. Do not report deployment complete from a successful build alone.


## Forwarding and startup guards (review update)

`ReverseProxy__KnownProxies__0` accepts a verified proxy IP; `ReverseProxy__KnownNetworks__0` accepts a verified CIDR. No trust is enabled by default. Do not copy a broad private range from an example. Confirm Render's actual ingress topology and the narrowest supported trusted range before enabling forwarding. Render outbound ranges are not ingress proxy ranges. Keep `ReverseProxy__ForwardLimit=1` unless the verified topology needs more hops (maximum 5). Global CIDRs are refused. `ASPNETCORE_FORWARDEDHEADERS_ENABLED` must remain unset.

After deployment, temporarily inspect the resolved client IP with synthetic requests from two clients and a forged leftmost forwarded entry, then remove diagnostic logging. Do not log OTPs, tokens or patient data. Production/deployment remains blocked until this check proves distinct legitimate clients and rejects spoofed addresses. No broad CIDR is preconfigured by this change.

Outside Development, JWT keys containing the public development markers are refused. Use a private generated signing key, not a value copied from development settings.

Hosted synthetic demo mode raises the shared per-IP OTP allowance to at least 200/hour while keeping the existing per-phone limits. This mitigates the website server-side proxy bucket; it does not establish real-client-IP forwarding or remove the need to verify trust topology. Production limits are unchanged. The concurrent refund test now permits either the patient cancellation or one staff refund to win. It still requires exactly one provider call, final Refunded state, only 200-Refunded or 409 staff responses, at most one successful staff response, and successful patient cancellation. PaymentService is unchanged.

### Measured forwarding on Render (backend-test)

Measured with a temporary log (removed). The app's direct sender is loopback (`::1`). `X-Forwarded-For` arrives as `[any client-supplied entries, real client, Cloudflare address, 10.x Render hop]`; `CF-Connecting-IP` is the real client. The 10.x hop varied between 10.24, 10.27 and 10.28. A forged leftmost entry stays leftmost. Trusting the real client therefore needs `ReverseProxy__KnownProxies__0=::1`, the 10.x hop and Cloudflare's published ranges in `ReverseProxy__KnownNetworks__N`, and `ReverseProxy__ForwardLimit=3`. Limit 1 or 2 returns the hop address, not the client. Nothing is enabled by default.
