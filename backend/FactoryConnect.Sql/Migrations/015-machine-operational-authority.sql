ALTER TABLE security.SessionAuthority ADD CONSTRAINT UQ_Session_OperationalProposer UNIQUE(TokenHash,PrincipalId);
CREATE TABLE configuration.MachineEnableProposal (
    ProposalId uniqueidentifier NOT NULL PRIMARY KEY CHECK(ProposalId<>'00000000-0000-0000-0000-000000000000'),
    SiteId uniqueidentifier NOT NULL,MachineId uniqueidentifier NOT NULL,
    ExpectedStateId uniqueidentifier NULL,ExpectedGeneration bigint NOT NULL CHECK(ExpectedGeneration>=0),
    ProposerId uniqueidentifier NOT NULL,ProposerSessionHash binary(32) NOT NULL,
    PrincipalKind char(1) NOT NULL DEFAULT 'H' CHECK(PrincipalKind='H'),
    IssuedAt datetimeoffset(7) NOT NULL,ExpiresAt datetimeoffset(7) NOT NULL,
    OperationId uniqueidentifier NOT NULL UNIQUE,
    FOREIGN KEY(SiteId,MachineId) REFERENCES [master].Machine(SiteId,MachineId),
    FOREIGN KEY(ProposerId,PrincipalKind) REFERENCES security.Principal(PrincipalId,Kind),
    FOREIGN KEY(ProposerSessionHash,ProposerId) REFERENCES security.SessionAuthority(TokenHash,PrincipalId),
    FOREIGN KEY(OperationId,ProposerId) REFERENCES security.SecurityOperation(OperationId,ActorPrincipalId),
    CONSTRAINT UQ_EnableProposal_Scope UNIQUE(ProposalId,SiteId,MachineId,ProposerId),
    CHECK((ExpectedStateId IS NULL AND ExpectedGeneration=0) OR (ExpectedStateId IS NOT NULL AND ExpectedGeneration>0)),
    CHECK(DATEPART(TZOFFSET,IssuedAt)=0 AND DATEPART(TZOFFSET,ExpiresAt)=0 AND ExpiresAt>IssuedAt AND ExpiresAt<=DATEADD(minute,15,IssuedAt))
);
CREATE TABLE configuration.MachineOperationalTransition (
    StateId uniqueidentifier NOT NULL PRIMARY KEY CHECK(StateId<>'00000000-0000-0000-0000-000000000000'),
    SiteId uniqueidentifier NOT NULL,MachineId uniqueidentifier NOT NULL,Generation bigint NOT NULL CHECK(Generation>0),
    Status varchar(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
    PriorStateId uniqueidentifier NULL,ProposalId uniqueidentifier NULL,EnablingProposerId uniqueidentifier NULL,
    ActorPrincipalId uniqueidentifier NOT NULL,PrincipalKind char(1) NOT NULL DEFAULT 'H' CHECK(PrincipalKind='H'),
    EffectiveFrom datetimeoffset(7) NOT NULL CHECK(DATEPART(TZOFFSET,EffectiveFrom)=0),
    OperationId uniqueidentifier NOT NULL UNIQUE,
    FOREIGN KEY(SiteId,MachineId) REFERENCES [master].Machine(SiteId,MachineId),
    FOREIGN KEY(ActorPrincipalId,PrincipalKind) REFERENCES security.Principal(PrincipalId,Kind),
    FOREIGN KEY(OperationId,ActorPrincipalId) REFERENCES security.SecurityOperation(OperationId,ActorPrincipalId),
    FOREIGN KEY(ProposalId,SiteId,MachineId,EnablingProposerId) REFERENCES configuration.MachineEnableProposal(ProposalId,SiteId,MachineId,ProposerId),
    CONSTRAINT UQ_Operational_Generation UNIQUE(SiteId,MachineId,Generation),
    CONSTRAINT UQ_Operational_Head UNIQUE(SiteId,MachineId,StateId,Generation),
    CONSTRAINT UQ_Operational_Scope UNIQUE(SiteId,MachineId,StateId),
    FOREIGN KEY(SiteId,MachineId,PriorStateId) REFERENCES configuration.MachineOperationalTransition(SiteId,MachineId,StateId),
    CHECK(PriorStateId IS NULL OR PriorStateId<>StateId),
    CHECK((Status='enabled' AND DATALENGTH(Status)=7 AND ProposalId IS NOT NULL AND EnablingProposerId IS NOT NULL AND EnablingProposerId<>ActorPrincipalId) OR
          (Status='suspended' AND DATALENGTH(Status)=9 AND ProposalId IS NULL AND EnablingProposerId IS NULL))
);
CREATE UNIQUE INDEX UQ_Operational_Proposal ON configuration.MachineOperationalTransition(ProposalId) WHERE ProposalId IS NOT NULL;
ALTER TABLE configuration.MachineEnableProposal ADD CONSTRAINT FK_EnableProposal_Prior
    FOREIGN KEY(SiteId,MachineId,ExpectedStateId,ExpectedGeneration) REFERENCES configuration.MachineOperationalTransition(SiteId,MachineId,StateId,Generation);
CREATE TABLE configuration.MachineOperationalState (
    SiteId uniqueidentifier NOT NULL,MachineId uniqueidentifier NOT NULL PRIMARY KEY,StateId uniqueidentifier NOT NULL,Generation bigint NOT NULL,
    FOREIGN KEY(SiteId,MachineId,StateId,Generation) REFERENCES configuration.MachineOperationalTransition(SiteId,MachineId,StateId,Generation)
);
-- Zero denotes legacy/unverified authority; no operational enablement is inferred during upgrade.
ALTER TABLE configuration.ActivationRequest ADD OperationalGeneration bigint NOT NULL CONSTRAINT DF_Activation_OperationalGeneration DEFAULT 0 CHECK(OperationalGeneration>=0);
ALTER TABLE configuration.RuntimeActivationAcknowledgement ADD OperationalGeneration bigint NOT NULL CONSTRAINT DF_Ack_OperationalGeneration DEFAULT 0 CHECK(OperationalGeneration>=0);
ALTER TABLE configuration.ActivationRequest ADD CONSTRAINT UQ_Activation_OperationalPin UNIQUE(SiteId,MachineId,ActivationId,ActivationGeneration,OperationalGeneration);
ALTER TABLE configuration.RuntimeActivationAcknowledgement ADD CONSTRAINT FK_Ack_OperationalPin
    FOREIGN KEY(SiteId,MachineId,ActivationId,ActivationGeneration,OperationalGeneration)
    REFERENCES configuration.ActivationRequest(SiteId,MachineId,ActivationId,ActivationGeneration,OperationalGeneration);
GO
CREATE TRIGGER configuration.TR_MachineEnableProposal_Immutable ON configuration.MachineEnableProposal INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51040,'Machine enable proposal is immutable.',1; END;
GO
CREATE TRIGGER configuration.TR_MachineOperationalTransition_Immutable ON configuration.MachineOperationalTransition INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51040,'Machine operational history is immutable.',1; END;
