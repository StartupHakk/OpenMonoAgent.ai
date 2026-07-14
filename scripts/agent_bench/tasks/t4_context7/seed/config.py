"""Server config model. Written against pydantic v1 — does not run on the installed pydantic."""
from pydantic import BaseModel, Field, root_validator, validator


class ServerConfig(BaseModel):
    host: str = Field(..., regex=r"^[a-z0-9.\-]+$")
    port: int
    tls: bool = False

    @validator("port")
    def port_unprivileged(cls, v):
        if v < 1024:
            raise ValueError("port must be >= 1024")
        return v

    @root_validator
    def tls_needs_high_port(cls, values):
        if values.get("tls") and values.get("port", 0) < 8443:
            raise ValueError("tls requires port >= 8443")
        return values


def load(data):
    """Validate a config dict and return it back as a plain dict."""
    return ServerConfig.parse_obj(data).dict()
