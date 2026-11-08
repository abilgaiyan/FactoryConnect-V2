        var expected=proof.Batches.ToDictionary(x=>x.BatchId);
        if(proof.AnchorWitness is {} anchor)
        {
            if(expected.TryGetValue(anchor.BatchId,out var existing))
            {
                if(existing!=anchor)throw new InvalidDataException("Version-4 anchor witness conflicts with source batch.");
            }
            else expected.Add(anchor.BatchId,anchor);
        }
        if((receipt.Checkpoint.OpenAnchor is null)!=(proof.AnchorWitness is null) ||
            proof.AnchorWitness is {} anchored &&
            (source.OpenAnchorObservation?.BatchId!=anchored.BatchId ||
             source.OpenAnchorObservation.DocumentSha256!=anchored.DocumentSha256))
            throw new InvalidDataException("Version-4 open-anchor witness mismatch.");
        var found=new HashSet<Guid>();
        if(prefetchedWitnesses is not null)
        {
            foreach(var witness in prefetchedWitnesses)
            {
                if(found.Count>=101 || !expected.TryGetValue(witness.BatchId,out var pin) ||
                    !found.Add(witness.BatchId) ||
                    !string.Equals(pin.DocumentSha256,Convert.ToHexString(witness.DocumentHash),StringComparison.Ordinal) ||
                    !string.Equals(pin.RequestSha256,Convert.ToHexString(witness.RequestHash),StringComparison.Ordinal))
                    throw new InvalidDataException("Version-4 publication source witness mismatch.");
            }
        }
        else
        {
        await using var witnesses=Command(c,t,"""
            SELECT TOP (102) w.BatchId,w.DocumentHash,w.IngestionRequestHash
            FROM activity.PublicationSourceWitness w WITH(HOLDLOCK)
            WHERE w.OperationId=@op AND w.MachineId=@machine AND w.StreamKeyBytes=@stream
            ORDER BY w.BatchId;
            """);
        Add(witnesses,("@op",operationId),("@machine",identity.MachineId),
            ("@stream",ActivityUtf8.GetBytes(identity.StreamKey)));
        witnesses.Parameters["@stream"].Size=512;
            await using var wr=await witnesses.ExecuteReaderAsync(ct);
            while(await wr.ReadAsync(ct))
            {
                if(found.Count>=101)throw new InvalidDataException("Version-4 source witness bound exceeded.");
                var batch=wr.GetGuid(0);
                if(!expected.TryGetValue(batch,out var pin) || !found.Add(batch) ||
                    !string.Equals(pin.DocumentSha256,Convert.ToHexString((byte[])wr[1]),StringComparison.Ordinal) ||
                    !string.Equals(pin.RequestSha256,Convert.ToHexString((byte[])wr[2]),StringComparison.Ordinal))
                    throw new InvalidDataException("Version-4 publication source witness mismatch.");
            }
        }
        if(found.Count!=expected.Count)throw new InvalidDataException("Version-4 publication source witness missing.");
        }
        catch(JsonException e){throw new InvalidDataException("Malformed activity publication receipt JSON.",e);}
        catch(KeyNotFoundException e){throw new InvalidDataException("Activity publication receipt property missing.",e);}
        catch(InvalidOperationException e){throw new InvalidDataException("Invalid activity publication receipt property type.",e);}
        catch(FormatException e){throw new InvalidDataException("Invalid activity publication receipt property value.",e);}
        catch(OverflowException e){throw new InvalidDataException("Invalid activity publication receipt numeric value.",e);}
    }
}
