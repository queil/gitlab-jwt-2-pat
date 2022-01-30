# gitlab-jwt-2-pat

Enables issuing short-lived impersonation tokens for the user authenticated in GitLab CI pipelines (via `$CI_JOB_JWT`/`$CI_JOB_JWT_V2`) token. It makes possible running actions like auto-tagging as the original user. GitLab PATs (Personal Access Token), including the impersonation tokens, do not support expiration times shorter than a day so the server tracks all issued tokens and automatically revokes them after the configured time.

[Docker image](https://github.com/queil/gitlab-jwt-2-pat/pkgs/container/gitlab-jwt-2-pat)

## :warning: Warning

This project is experimental - use at your own risk. Hopefully GitLab makes a similar functionality [built-in at some point](https://gitlab.com/groups/gitlab-org/-/epics/3559). Also please note that GitLab's functionality around the JWTs is being under active development. Search for `CI_JOB_JWT` and `CI_JOB_JWT_V2` in [predefined variables](https://docs.gitlab.com/ee/ci/variables/predefined_variables.html) for more info.

## The basics

The server exposes two endpoints: 

* `/token` - this is the main working endpoint requiring a standard `Authorization: Bearer your-encoded-token-here` header. The JWT token provided in the header gets validated (making sure the token is not expired and comes from a legitimate issuer - i.e. your GitLab instance). :warning: Token audience validation must be turned off (via `JWT__VALIDATE__AUDIENCE=false`) for JWT tokens issued by GitLab version < 14.7 because it doesn't contain the `aud` claim. It is being fixed in the `CI_JOB_JWT_V2` which will become the default in the future but gets released as an alpha feature in GitLab 14.7.

* `/health` - an endpoint that can be used for health-checks (e.g. in Kubernetes)

## Example usage in CI

```bash
git push https://$GITLAB_USER_LOGIN:$(curl -sS --fail-with-body -H "Authorization: Bearer $CI_JOB_JWT" https://gitlab-jtp.example.com/token)@$CI_SERVER_HOST/$CI_PROJECT_PATH.git HEAD:$CI_COMMIT_REF_NAME
```

## Configuration

* `GITLAB__HOSTNAME` - sets GitLab's instance hostname
* `GITLAB__APIKEY` - it needs to be an admin user's PAT so it can issue/revoke impersonation tokens.
* `GITLAB__SUDOUSERLOGIN` - impersonation tokens can only be issued by an admin user with [sudo](https://docs.gitlab.com/ee/api/#sudo) enabled. It requires the sudo user name to be sent together with the query string.

* `GITLAB__TOKENCONFIG__SCOPES__0` - defines [scopes](https://docs.gitlab.com/ee/api/users.html#create-an-impersonation-token) for the created impersonation token. Multiple values can be specified by specifying this variable multiple times incrementing the array index.

* `GITLAB__TOKENCONFIG__REVOKESECONDS` - declares how many seconds after issuance the token should be revoked. The value should be quite low (like a few seconds) to improve security. Values greater than 24h won't have any effect as the impersonation tokens that get issued are only valid until midnight anyway (via setting `expires_at` in [the API call](https://docs.gitlab.com/ee/api/users.html#create-an-impersonation-token))

JWT settings:

* `JWT__ISSUER` - JWT's issuer gets validated against the specified value
* `JWT__AUTHORITY` - used to retrieve OIDC metadata (from `$JWT__AUTHORITY/.well-known/openid-configuration`)
* `JWT__DEBUG` - if set to true encoded JWTs gets logged to stdout. Also make sure you set `Logging__LogLevel__Default` to `Debug` otherwise the tokens won't be logged. Default: `false`.
* `JWT__VALIDATE__AUDIENCE` - needs to be set to false for GitLab version < 14.7 as the JWT token in earlier versions doesn't contain `aud`. 

Other settings:

* `ASPNETCORE_URLS` - sets the IP and port for the server (example: `http://*:5000`)


### Example config

```bash
GITLAB__HOSTNAME=https://gitlab.example.com
GITLAB__SUDOUSERLOGIN=your-token-issuer-user
GITLAB__TOKENCONFIG__SCOPES__0=write_repository
GITLAB__TOKENCONFIG__SCOPES__1=api
GITLAB__TOKENCONFIG__REVOKESECONDS=30
JWT__ISSUER=gitlab.example.com
JWT__AUTHORITY=https://gitlab.example.com
JWT__DEBUG=true
JWT__VALIDATE__AUDIENCE=false
COMPlus_EnableDiagnostics=0
Logging__LogLevel__Default=Information
Logging__LogLevel__System=Error
Logging__LogLevel__Microsoft=Error
ASPNETCORE_URLS=http://*:5000
```

## Credits

[Johann Gyger](https://gitlab.com/johanngyger) - author of [GiLP](https://gitlab.com/johanngyger/gilp)
