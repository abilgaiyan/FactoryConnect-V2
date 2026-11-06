CREATE TABLE security.HumanSessionIssuance (
    OperationId uniqueidentifier NOT NULL PRIMARY KEY REFERENCES security.SecurityOperation(OperationId),
    ProofHash binary(32) NOT NULL UNIQUE,
    TokenHash binary(32) NOT NULL UNIQUE REFERENCES security.SessionAuthority(TokenHash),
    AuthenticatedAt datetimeoffset(7) NOT NULL,
    ProofExpiresAt datetimeoffset(7) NOT NULL,
    CONSTRAINT CK_HumanSession_ProofWindow CHECK(ProofExpiresAt>AuthenticatedAt)
);
GO
CREATE TRIGGER security.TR_HumanSessionIssuance_Immutable ON security.HumanSessionIssuance INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51030,'Session issuance history is immutable.',1; END;
