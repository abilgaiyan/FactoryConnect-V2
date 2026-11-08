-- Legacy receipts retain NULL ResultHash and their original replay contract.
ALTER TABLE activity.ProcessorOperation ADD ResultHash binary(32) NULL;
ALTER TABLE activity.ProcessorOperation ADD CONSTRAINT CK_ActivityOperationResultHash
    CHECK(ResultHash IS NULL OR ResultHash=HASHBYTES('SHA2_256',ResultBytes));
ALTER TABLE activity.ProcessorOperation ADD CONSTRAINT UQ_ActivityPublicationReceipt
    UNIQUE(OperationId,MachineId,StreamKeyBytes,MappingId,AppliedGeneration,RequestHash,ResultHash);
CREATE TABLE activity.PublicationReceiptAncestry (
    OperationId uniqueidentifier NOT NULL PRIMARY KEY,
    MachineId uniqueidentifier NOT NULL,StreamKeyBytes varbinary(512) NOT NULL,MappingId uniqueidentifier NOT NULL,
    Generation bigint NOT NULL,RequestHash binary(32) NOT NULL,ResultHash binary(32) NOT NULL,ProofHash binary(32) NOT NULL,
    ParentOperationId uniqueidentifier NULL,ParentGeneration bigint NULL,
    ParentRequestHash binary(32) NULL,ParentResultHash binary(32) NULL,ParentProofHash binary(32) NULL,
    CONSTRAINT UQ_PublicationReceiptAncestry UNIQUE(OperationId,MachineId,StreamKeyBytes,MappingId,Generation,RequestHash,ResultHash,ProofHash),
    CONSTRAINT FK_PublicationReceipt_Operation FOREIGN KEY(OperationId,MachineId,StreamKeyBytes,MappingId,Generation,RequestHash,ResultHash)
        REFERENCES activity.ProcessorOperation(OperationId,MachineId,StreamKeyBytes,MappingId,AppliedGeneration,RequestHash,ResultHash),
    CONSTRAINT FK_PublicationReceipt_Parent FOREIGN KEY(ParentOperationId,MachineId,StreamKeyBytes,MappingId,ParentGeneration,ParentRequestHash,ParentResultHash,ParentProofHash)
        REFERENCES activity.PublicationReceiptAncestry(OperationId,MachineId,StreamKeyBytes,MappingId,Generation,RequestHash,ResultHash,ProofHash),
    CONSTRAINT CK_PublicationReceipt_Parent CHECK(
        (Generation=1 AND ParentOperationId IS NULL AND ParentGeneration IS NULL AND ParentRequestHash IS NULL AND ParentResultHash IS NULL AND ParentProofHash IS NULL) OR
        (Generation>1 AND ParentOperationId IS NOT NULL AND ParentGeneration IS NOT NULL AND ParentGeneration=Generation-1 AND ParentRequestHash IS NOT NULL AND ParentResultHash IS NOT NULL AND ParentProofHash IS NOT NULL))
);
GO
CREATE TRIGGER activity.TR_PublicationReceiptAncestryImmutable ON activity.PublicationReceiptAncestry INSTEAD OF UPDATE,DELETE AS
BEGIN THROW 51040,'Publication receipt ancestry is immutable.',1; END;
